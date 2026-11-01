using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Auth.Authorization;
using Auth.Caching;
using Auth.Clients;
using Auth.Clients.Caching;
using Auth.Claims;
using Auth.EntityFrameworkCore;
using Auth.Middleware;
using Auth.Observability;
using Auth.Pages.Account.Validation;
using Auth.RateLimiting;
using Auth.Routing;
using Auth.TokenMetadata;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using Quartz;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.Caching;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.EventBus;
using Shared.Permissions.Definitions.Auth;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;
using Shared.Security;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace Auth;

internal static class AuthHostRegistrationExtensions
{
    public static void AddAuthHostServices(this ServiceConfigurationContext context)
    {
        context.Services.AddTenantWrightMessagingHost<AuthDbContext>(
            context.Configuration,
            Shared.EventBus.Messaging.TenantWrightMessagingTopology.AuthPrincipal);
        var services = context.Services;
        var configuration = context.Configuration;
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        if (!isDevelopment)
        {
            var allowInsecureInternalHttp = string.Equals(
                configuration["Services:AllowInsecureInternalHttp"],
                "true",
                StringComparison.OrdinalIgnoreCase);

            var authority = configuration["AuthServer:Authority"];
            if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "AuthServer:Authority",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-auth/values.yaml to an https URL and redeploy.");
            }

            var issuer = configuration["OpenIddict:Issuer"];
            if (string.IsNullOrWhiteSpace(issuer) || !issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "OpenIddict:Issuer",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'OpenIddict__Issuer' in charts/tenantwright-auth/values.yaml to an https URL and redeploy.");
            }

            AuthHostModule.ValidateInternalServiceUrl(
                "Services:Identity:Url",
                configuration["Services:Identity:Url"],
                allowInsecureInternalHttp);

            AuthHostModule.ValidateInternalServiceUrl(
                "Services:Tenant:Url",
                configuration["Services:Tenant:Url"],
                allowInsecureInternalHttp);

            AuthHostModule.ValidateInternalServiceUrl(
                "Services:Permissions:Url",
                configuration["Services:Permissions:Url"] ?? configuration["RemoteFeatureChecker:BaseAddress"],
                allowInsecureInternalHttp);

            var internalClientId = configuration["OpenIddict:Clients:Internal:ClientId"];
            var internalClientSecret = configuration["OpenIddict:Clients:Internal:Secret"];
            if (string.IsNullOrWhiteSpace(internalClientId) || string.IsNullOrWhiteSpace(internalClientSecret))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "OpenIddict:Clients:Internal:ClientId|OpenIddict:Clients:Internal:Secret",
                    rule: "ClientId and Secret are required outside Development.",
                    fixHint: "Provide 'OpenIddict__Clients__Internal__ClientId' and map 'OpenIddict__Clients__Internal__Secret' from secret 'tenantwright-auth-secrets.openiddict-internal-client-secret' in charts/tenantwright-auth/values.yaml.");
            }

            AuthHostModule.ValidatePublicBaseUrl(configuration["Auth:PublicBaseUrl"]);

            var authDb = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(authDb))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required outside Development.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-auth-db.connection-string' in charts/tenantwright-auth/values.yaml and ensure the secret exists.");
            }

            var signingCertPassword = configuration["OpenIddict:SigningCertificate:Password"];
            var encryptionCertPassword = configuration["OpenIddict:EncryptionCertificate:Password"];
            if (string.IsNullOrWhiteSpace(signingCertPassword) || string.IsNullOrWhiteSpace(encryptionCertPassword))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "OpenIddict:SigningCertificate:Password|OpenIddict:EncryptionCertificate:Password",
                    rule: "Both OpenIddict certificate passwords are required outside Development.",
                    fixHint: "Set secret refs for 'OpenIddict__SigningCertificate__Password' and 'OpenIddict__EncryptionCertificate__Password' from secret 'tenantwright-auth-secrets'.");
            }
        }

        services.AddControllers();
        services.AddRazorPages(options =>
        {
            AuthRazorPageConventions.Apply(options);
        });
        services.AddValidatorsFromAssemblyContaining<MfaInputModelValidator>();
        services.AddSingleton<IAuthPasswordPolicyProvider, AuthPasswordPolicyProvider>();
        services.AddScoped<IAuthPageValidationService, AuthPageValidationService>();
        services.AddOptions<Registration.PublicTenantRegistrationOptions>()
            .Bind(configuration.GetSection("Auth:PublicTenantRegistration"))
            .Validate(options => !options.Enabled || (!string.IsNullOrWhiteSpace(options.TurnstileSiteKey) && !string.IsNullOrWhiteSpace(options.TurnstileSecret) && !string.IsNullOrWhiteSpace(options.HashKey)),
                "Auth:PublicTenantRegistration requires TurnstileSiteKey, TurnstileSecret, and HashKey when enabled.")
            .Validate(
                options => isDevelopment || !options.UsesCloudflareTestCredentials(),
                "Cloudflare Turnstile test credentials are allowed only in Development.")
            .ValidateOnStart();
        services.AddScoped<Registration.IPublicTenantRegistrationGate, Registration.PublicTenantRegistrationGate>();
        services.AddOptions<AccessTokenSecurityOptions>()
            .Bind(configuration.GetSection(AccessTokenSecurityOptions.SectionName))
            .Validate(
                AccessTokenSecurityOptions.IsValid,
                $"{AccessTokenSecurityOptions.SectionName} must preserve the approved 6 KiB serialized access-token and 32-role limits.")
            .ValidateOnStart();
        services.AddScoped<RoleClaimsPrincipalService>();
        services.AddHttpClient("turnstile", client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddHttpContextAccessor();
        services.AddSingleton(typeof(IAuthHybridCache<>), typeof(AuthHybridCache<>));

        services.AddDataProtection()
            .SetApplicationName(configuration["DataProtection:ApplicationName"] ?? "TenantWright.Auth");

        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
        {
            return new ConfigureOptions<KeyManagementOptions>(options =>
            {
                var multiplexer = sp.GetService<IConnectionMultiplexer>();
                if (multiplexer == null)
                {
                    return;
                }

                var valkeyKey = configuration["DataProtection:ValkeyKey"] ?? "DataProtection-Keys";
                options.XmlRepository = new RedisXmlRepository(() => multiplexer.GetDatabase(), valkeyKey);
            });
        });

        services.Configure<TokenMetadataCacheOptions>(configuration.GetSection("Auth:TokenMetadataCache"));
        services.Configure<TokenRateLimitingOptions>(configuration.GetSection("Auth:RateLimiting:Token"));

        var authLoginPermitLimit = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:Login:PermitLimit") ?? 20, 1, 500);
        var authLoginWindowSeconds = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:Login:WindowSeconds") ?? 60, 5, 3600);
        var authMfaPermitLimit = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:Mfa:PermitLimit") ?? 12, 1, 500);
        var authMfaWindowSeconds = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:Mfa:WindowSeconds") ?? 60, 5, 3600);
        var authResetPermitLimit = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:Reset:PermitLimit") ?? 8, 1, 500);
        var authResetWindowSeconds = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:Reset:WindowSeconds") ?? 300, 5, 3600);
        var managementPermitLimit = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:OpenIddictManagement:PermitLimit") ?? 30, 1, 500);
        var managementWindowSeconds = Math.Clamp(configuration.GetValue<int?>("Auth:RateLimiting:OpenIddictManagement:WindowSeconds") ?? 60, 5, 3600);

        services.AddSingleton<Auth.Managers.OpenIddictManagementValidator>();
        services.AddScoped<Auth.Managers.OpenIddictManagementManager>();

        services.AddTransient<IOpenIddictTokenMetadataSource, OpenIddictTokenMetadataSource>();
        services.AddSingleton<ITokenMetadataStampStore, TokenMetadataStampStore>();
        services.AddSingleton<TokenMetadataCacheService>();
        services.AddSingleton<ITokenMetadataReadService>(sp => sp.GetRequiredService<TokenMetadataCacheService>());
        services.AddSingleton<ITokenMetadataInvalidationService>(sp => sp.GetRequiredService<TokenMetadataCacheService>());
        services.AddSingleton<ITokenMetadataWarmupService>(sp => sp.GetRequiredService<TokenMetadataCacheService>());
        services.AddSingleton<TokenDbQueryMetricsInterceptor>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var rateLimitOptions = TokenRateLimiterKeyResolver.Normalize(
                    context.HttpContext.RequestServices.GetRequiredService<IOptions<TokenRateLimitingOptions>>().Value);
                var requestInfo = TokenRateLimiterKeyResolver.ResolveForRejection(context.HttpContext, rateLimitOptions);
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<AuthHostModule>>();
                var policyName = context.HttpContext.GetEndpoint()
                    ?.Metadata.GetMetadata<EnableRateLimitingAttribute>()
                    ?.PolicyName ?? "unknown";

                logger.LogWarning(
                    "Auth rate limit rejected. Policy={Policy}, ClientIdHash={ClientIdHash}, GrantType={GrantType}, PartitionMode={PartitionMode}, PartitionHash={PartitionHash}",
                    policyName,
                    TokenRateLimiterKeyResolver.HashClientId(requestInfo.ClientId),
                    requestInfo.GrantType ?? "unknown",
                    rateLimitOptions.PartitionMode,
                    TokenRateLimiterKeyResolver.HashPartitionKey(requestInfo.PartitionKey));

                return ValueTask.CompletedTask;
            };

            options.AddPolicy("token", httpContext =>
            {
                var rateLimitOptions = TokenRateLimiterKeyResolver.Normalize(
                    httpContext.RequestServices.GetRequiredService<IOptions<TokenRateLimitingOptions>>().Value);
                var requestInfo = TokenRateLimiterKeyResolver.ResolveForPartition(httpContext, rateLimitOptions);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: requestInfo.PartitionKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitOptions.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimitOptions.WindowSeconds),
                        QueueLimit = rateLimitOptions.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            options.AddPolicy("openiddict-management", httpContext =>
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildOpenIddictManagementPartitionKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = managementPermitLimit,
                        Window = TimeSpan.FromSeconds(managementWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            options.AddPolicy("auth-login", httpContext =>
            {
                if (!AuthInteractiveRateLimiter.ShouldLimit(httpContext))
                {
                    return RateLimitPartition.GetNoLimiter(BuildInteractiveAuthPartitionKey(httpContext, "auth-login"));
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildInteractiveAuthPartitionKey(httpContext, "auth-login"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authLoginPermitLimit,
                        Window = TimeSpan.FromSeconds(authLoginWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            options.AddPolicy("auth-mfa", httpContext =>
            {
                if (!AuthInteractiveRateLimiter.ShouldLimit(httpContext))
                {
                    return RateLimitPartition.GetNoLimiter(BuildInteractiveAuthPartitionKey(httpContext, "auth-mfa"));
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildInteractiveAuthPartitionKey(httpContext, "auth-mfa"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authMfaPermitLimit,
                        Window = TimeSpan.FromSeconds(authMfaWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            options.AddPolicy("auth-reset", httpContext =>
            {
                if (!AuthInteractiveRateLimiter.ShouldLimit(httpContext))
                {
                    return RateLimitPartition.GetNoLimiter(BuildInteractiveAuthPartitionKey(httpContext, "auth-reset"));
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildInteractiveAuthPartitionKey(httpContext, "auth-reset"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authResetPermitLimit,
                        Window = TimeSpan.FromSeconds(authResetWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });
        });

        services.AddSingleton<IAuthorizationHandler, OpenIddictManagementPermissionHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var (policyName, permissionName) in OpenIddictManagementPolicies.Mappings)
            {
                options.AddPolicy(policyName, policy =>
                {
                    policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new OpenIddictManagementPermissionRequirement(permissionName));
                });
            }
        });

        services.AddDbContext<AuthDbContext>((sp, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.UseOpenIddict();
            options.AddInterceptors(sp.GetRequiredService<TokenDbQueryMetricsInterceptor>());
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = AuthPageRoutes.SignIn;
                options.LogoutPath = AuthPageRoutes.Logout;
                options.AccessDeniedPath = AuthPageRoutes.AccessDenied;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = async context =>
                    {
                        var principal = context.Principal;
                        if (principal == null)
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                            return;
                        }

                        var sessionId = principal.FindFirst(SharedClaimTypes.SessionId)?.Value;
                        var subject = principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                        if (!Guid.TryParse(subject, out var userId) || string.IsNullOrWhiteSpace(sessionId))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                            return;
                        }

                        Guid? tenantId = null;
                        var tenantIdClaim = principal.FindFirst(SharedClaimTypes.TenantId)?.Value;
                        if (Guid.TryParse(tenantIdClaim, out var parsedTenantId))
                        {
                            tenantId = parsedTenantId;
                        }

                        var identityClient = context.HttpContext.RequestServices.GetRequiredService<IIdentityClient>();
                        var isValid = await identityClient.ValidateSessionAsync(userId, sessionId, tenantId: tenantId, cancellationToken: context.HttpContext.RequestAborted);
                        if (!isValid)
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                            if (context.HttpContext.Request.Path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase))
                            {
                                context.HttpContext.Response.Redirect(AuthPageRoutes.SessionExpired);
                            }
                        }
                    }
                };
            });

        var useQuartzPersistentStore = configuration.GetValue<bool?>("Auth:Quartz:UsePersistentStore") ?? false;
        var quartzConnectionString = configuration.GetConnectionString("Default");

        services.AddQuartz(options =>
        {
            options.UseSimpleTypeLoader();

            if (!useQuartzPersistentStore)
            {
                options.UseInMemoryStore();
                return;
            }

            if (string.IsNullOrWhiteSpace(quartzConnectionString))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required when Auth:Quartz:UsePersistentStore=true.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-auth-db.connection-string' in charts/tenantwright-auth/values.yaml, or disable 'Auth:Quartz:UsePersistentStore'.");
            }

            options.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.PerformSchemaValidation = configuration.GetValue<bool?>("Auth:Quartz:PerformSchemaValidation") ?? false;
                store.RetryInterval = TimeSpan.FromSeconds(15);
                store.UsePostgres(db =>
                {
                    db.ConnectionString = quartzConnectionString;
                    db.TablePrefix = "QRTZ_";
                });
                store.UseBinarySerializer();
            });
        });

        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                       .UseDbContext<AuthDbContext>();

                options.UseQuartz();
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/connect/authorize")
                       .SetTokenEndpointUris("/connect/token")
                       .SetEndSessionEndpointUris("/connect/logout")
                       .SetUserInfoEndpointUris("/connect/userinfo")
                       .SetRevocationEndpointUris("/connect/revoke");

                options.AllowAuthorizationCodeFlow()
                       .AllowRefreshTokenFlow()
                       .RequireProofKeyForCodeExchange();

                var acceptAnonymousClients =
                    string.Equals(configuration["OpenIddict:AcceptAnonymousClients"], "true", StringComparison.OrdinalIgnoreCase);
                if (acceptAnonymousClients)
                {
                    if (!isDevelopment)
                    {
                        throw StartupConfigExceptionFactory.InvalidConfiguration(
                            service: "tenantwright-auth",
                            key: "OpenIddict:AcceptAnonymousClients",
                            rule: "Must be disabled outside Development.",
                            fixHint: "Set 'OpenIddict__AcceptAnonymousClients=false' in charts/tenantwright-auth/values.yaml (or appsettings) and redeploy.");
                    }

                    options.AcceptAnonymousClients();
                }

                if (AuthPasswordFlowPolicy.ResolveEnabled(configuration, isDevelopment))
                {
                    options.AllowPasswordFlow();
                }

                options.AllowClientCredentialsFlow();
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(7));
                var signingCertPath = configuration["OpenIddict:SigningCertificate:Path"];
                var signingCertPassword = configuration["OpenIddict:SigningCertificate:Password"];
                var encryptionCertPath = configuration["OpenIddict:EncryptionCertificate:Path"];
                var encryptionCertPassword = configuration["OpenIddict:EncryptionCertificate:Password"];
                var contentRootPath = Directory.GetCurrentDirectory();

                static string? ResolvePath(string? path, string contentRoot)
                {
                    if (string.IsNullOrWhiteSpace(path)) return null;
                    return Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);
                }

                if (isDevelopment)
                {
                    var resolvedEncryptionCertPath = ResolvePath(encryptionCertPath, contentRootPath);
                    var resolvedSigningCertPath = ResolvePath(signingCertPath, contentRootPath);

                    if (!string.IsNullOrWhiteSpace(resolvedEncryptionCertPath) && !string.IsNullOrWhiteSpace(resolvedSigningCertPath)
                        && File.Exists(resolvedEncryptionCertPath) && File.Exists(resolvedSigningCertPath))
                    {
                        options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(resolvedEncryptionCertPath, encryptionCertPassword))
                               .AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(resolvedSigningCertPath, signingCertPassword));
                    }
                    else
                    {
                        options.AddDevelopmentEncryptionCertificate()
                               .AddDevelopmentSigningCertificate();
                    }
                }
                else
                {
                    var resolvedEncryptionCertPath = ResolvePath(encryptionCertPath, contentRootPath);
                    var resolvedSigningCertPath = ResolvePath(signingCertPath, contentRootPath);

                    if (string.IsNullOrWhiteSpace(resolvedEncryptionCertPath)) throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-auth",
                        key: "OpenIddict:EncryptionCertificate:Path",
                        rule: "Certificate path is required outside Development.",
                        fixHint: "Set env 'OpenIddict__EncryptionCertificate__Path' in charts/tenantwright-auth/values.yaml and mount secret 'tenantwright-auth-openiddict-certs' with file 'auth-encryption.pfx'.");
                    if (string.IsNullOrWhiteSpace(resolvedSigningCertPath)) throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-auth",
                        key: "OpenIddict:SigningCertificate:Path",
                        rule: "Certificate path is required outside Development.",
                        fixHint: "Set env 'OpenIddict__SigningCertificate__Path' in charts/tenantwright-auth/values.yaml and mount secret 'tenantwright-auth-openiddict-certs' with file 'auth-signing.pfx'.");
                    if (!File.Exists(resolvedEncryptionCertPath)) throw new FileNotFoundException("Encryption certificate file not found.", resolvedEncryptionCertPath);
                    if (!File.Exists(resolvedSigningCertPath)) throw new FileNotFoundException("Signing certificate file not found.", resolvedSigningCertPath);

                    options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(resolvedEncryptionCertPath, encryptionCertPassword))
                           .AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(resolvedSigningCertPath, signingCertPassword));
                }

                options.DisableAccessTokenEncryption();

                var aspNetCore = options.UseAspNetCore();

                if (isDevelopment)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }

                var issuer = configuration["OpenIddict:Issuer"];
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    options.SetIssuer(new Uri(issuer));
                }

                aspNetCore.EnableAuthorizationEndpointPassthrough()
                          .EnableTokenEndpointPassthrough()
                          .EnableEndSessionEndpointPassthrough()
                          .EnableUserInfoEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.Configure<OpenIddictServerOptions>(options =>
        {
            options.RefreshTokenReuseLeeway = TimeSpan.Zero;
            options.SubjectTokenTypes.Add(OpenIddictConstants.TokenTypeIdentifiers.AccessToken);
            options.RequestedTokenTypes.Add(OpenIddictConstants.TokenTypeIdentifiers.AccessToken);
            options.DefaultRequestedTokenType = OpenIddictConstants.TokenTypeIdentifiers.AccessToken;
        });

        services.Configure<OpenIddictCacheOptions>(configuration.GetSection("OpenIddictCache"));

        services.AddHttpClient<IIdentityClient, IdentityClient>()
            .AddStandardResilienceHandler();

        services.AddHttpClient<ITenantClient, TenantClient>()
            .AddStandardResilienceHandler();

        services.AddHttpClient<IInternalAccessTokenProvider, InternalAccessTokenProvider>()
            .AddStandardResilienceHandler();

        services.Configure<IdentityUserCacheOptions>(configuration.GetSection("IdentityUserCache"));

        services.Configure<SharedDataSeedOptions>(options =>
        {
            options.Contributors.Add<Auth.Data.OpenIddictDataSeedContributor>();
        });
    }

    private static string BuildOpenIddictManagementPartitionKey(HttpContext httpContext)
    {
        var clientId = httpContext.User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value ?? "unknown-client";
        var subject = httpContext.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value ?? "unknown-subject";
        var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-address";
        return $"openiddict-management:{TokenRateLimiterKeyResolver.HashClientId(clientId)}:{TokenRateLimiterKeyResolver.HashClientId(subject)}:{TokenRateLimiterKeyResolver.HashClientId(remoteAddress)}";
    }

    internal static string BuildInteractiveAuthPartitionKey(HttpContext httpContext, string policyName)
    {
        var tenantId = httpContext.RequestServices.GetRequiredService<ICurrentTenant>().Id;
        var tenant = tenantId?.ToString("D") ?? "unresolved";

        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrWhiteSpace(ip))
        {
            ip = "unknown";
        }

        var path = httpContext.Request.Path.HasValue ? httpContext.Request.Path.Value! : "/";
        return $"{policyName}:{tenant}:{ip}:{path}";
    }
}
