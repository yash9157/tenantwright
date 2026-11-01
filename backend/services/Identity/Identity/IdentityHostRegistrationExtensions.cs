using Identity.Application;
using Identity.Domain;
using Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Uow.Implementations;
using Shared.Permissions;
using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using System.Linq;
using Shared.Permissions.Features;
using Permissions.Application.Contracts.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Identity.Application.Account.Risk;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.SecuritySettings;
using Identity.Application.SecuritySettings;
using Identity.Domain.SecuritySettings;
using Identity.Domain.Shared.Account.Risk;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Identity.Security;
using Identity.Application.Users;
using Identity.Application.Contracts.Passkeys;
using Fido2NetLib;

namespace Identity;

internal static class IdentityHostRegistrationExtensions
{
    public static void AddIdentityHostServices(this ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = context.Configuration;
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        // Configure Feature Checker (HTTP Client)
        services.Configure<FeatureCheckerClientOptions>(configuration.GetSection("RemoteFeatureChecker"));
        services.AddHttpClient(nameof(HttpFeatureCheckerClient));
        services.TryAddTransient<IFeatureCheckerClient, HttpFeatureCheckerClient>();
        services.TryAddTransient<ILoginGeoResolver, HeaderLoginGeoResolver>();

        if (!isDevelopment)
        {
            var authority = configuration["AuthServer:Authority"];
            if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-identity",
                    key: "AuthServer:Authority",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-identity/values.yaml to an https URL and redeploy.");
            }

            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-identity",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required outside Development.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-identity-db.connection-string' in charts/tenantwright-identity/values.yaml and ensure the secret exists.");
            }
        }

        var lockoutMaxFailedAttempts = Math.Clamp(configuration.GetValue<int?>("Identity:Lockout:MaxFailedAccessAttempts") ?? 5, 3, 20);
        var lockoutMinutes = Math.Clamp(configuration.GetValue<int?>("Identity:Lockout:DefaultLockoutMinutes") ?? 15, 1, 1440);
        var lockoutAllowedForNewUsers = configuration.GetValue<bool?>("Identity:Lockout:AllowedForNewUsers") ?? true;
        var hostIdentitySecurityStaticOptions = IdentityHostModule.BuildHostIdentitySecurityStaticOptions(
            configuration,
            isDevelopment,
            lockoutAllowedForNewUsers,
            lockoutMaxFailedAttempts,
            lockoutMinutes);

        services.AddSingleton(hostIdentitySecurityStaticOptions);
        services.TryAddSingleton<HostIdentitySecurityPolicyRuntimeState>();

        services.Configure<IdentityOptions>(options =>
        {
            options.Password.RequireDigit = hostIdentitySecurityStaticOptions.DefaultSettings.Password.RequireDigit;
            options.Password.RequireLowercase = hostIdentitySecurityStaticOptions.DefaultSettings.Password.RequireLowercase;
            options.Password.RequireNonAlphanumeric = hostIdentitySecurityStaticOptions.DefaultSettings.Password.RequireNonAlphanumeric;
            options.Password.RequireUppercase = hostIdentitySecurityStaticOptions.DefaultSettings.Password.RequireUppercase;
            options.Password.RequiredLength = hostIdentitySecurityStaticOptions.DefaultSettings.Password.RequiredLength;
            options.User.RequireUniqueEmail = true;
            options.Lockout.MaxFailedAccessAttempts = hostIdentitySecurityStaticOptions.DefaultSettings.Lockout.MaxFailedAccessAttempts;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(hostIdentitySecurityStaticOptions.DefaultSettings.Lockout.DefaultLockoutMinutes);
            options.Lockout.AllowedForNewUsers = hostIdentitySecurityStaticOptions.DefaultSettings.Lockout.AllowedForNewUsers;
        });
        services.AddOptions<RoleMembershipPolicyOptions>()
            .Bind(configuration.GetSection(RoleMembershipPolicyOptions.SectionName))
            .Validate(
                RoleMembershipPolicyOptions.IsValid,
                $"{RoleMembershipPolicyOptions.SectionName} must preserve the approved 32-role limit.")
            .ValidateOnStart();

        services.AddOptions<Identity.Data.IdentityBootstrapSeedOptions>()
            .Bind(configuration.GetSection("BootstrapSeed"))
            .Validate(
                options =>
                    !options.Enabled ||
                    !options.HostAdmin.Enabled ||
                    (
                        !string.IsNullOrWhiteSpace(options.HostAdmin.UserName) &&
                        !string.IsNullOrWhiteSpace(options.HostAdmin.Email) &&
                        !string.IsNullOrWhiteSpace(options.HostAdmin.Password) &&
                        new EmailAddressAttribute().IsValid(options.HostAdmin.Email)
                    ),
                "BootstrapSeed:HostAdmin requires valid UserName, Email, and Password when bootstrap seeding is enabled.")
            .Validate(
                options =>
                    !options.Enabled ||
                    !options.TenantAdmin.Enabled ||
                    (
                        options.TenantAdmin.TenantId != Guid.Empty &&
                        !string.IsNullOrWhiteSpace(options.TenantAdmin.TenantName) &&
                        !string.IsNullOrWhiteSpace(options.TenantAdmin.UserName) &&
                        !string.IsNullOrWhiteSpace(options.TenantAdmin.Email) &&
                        !string.IsNullOrWhiteSpace(options.TenantAdmin.Password) &&
                        new EmailAddressAttribute().IsValid(options.TenantAdmin.Email)
                    ),
                "BootstrapSeed:TenantAdmin requires valid TenantId, TenantName, UserName, Email, and Password when tenant-admin bootstrap seeding is enabled.")
            .ValidateOnStart();

        services.AddOptions<PasskeyOptions>()
            .Bind(configuration.GetSection(PasskeyOptions.SectionName))
            .Validate(
                options => !options.Enabled ||
                           (!string.IsNullOrWhiteSpace(options.RelyingPartyId) &&
                            !options.RelyingPartyId.Contains("://", StringComparison.Ordinal)),
                "Passkeys:RelyingPartyId must be a bare relying-party domain when passkeys are enabled.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.RelyingPartyName),
                "Passkeys:RelyingPartyName is required when passkeys are enabled.")
            .Validate(
                options => !options.Enabled ||
                           (options.Origins.Count > 0 && options.Origins.All(IsValidPasskeyOrigin)),
                "Passkeys:Origins must contain only HTTPS origins (localhost HTTP is allowed for development).")
            .Validate(
                options => options.CeremonyTtlSeconds is >= 60 and <= 600,
                "Passkeys:CeremonyTtlSeconds must be between 60 and 600.")
            .Validate(
                options => isDevelopment || IsProductionPasskeyConfigurationValid(options),
                "Passkeys must use an explicit non-localhost relying-party ID and HTTPS origins outside Development.")
            .ValidateOnStart();

        var passkeyOptions = configuration.GetSection(PasskeyOptions.SectionName).Get<PasskeyOptions>() ?? new PasskeyOptions();
        services.AddFido2(options =>
        {
            options.ServerDomain = passkeyOptions.RelyingPartyId;
            options.ServerName = passkeyOptions.RelyingPartyName;
            options.Origins = passkeyOptions.Origins;
            options.TimestampDriftTolerance = 300000;
        });

        services.AddOptions<LoginRiskOptions>()
            .Bind(configuration.GetSection("LoginRisk"))
            .Validate(
                options => Enum.IsDefined(options.Mode),
                "LoginRisk:Mode is invalid. Use Observe or Enforce.")
            .Validate(
                options => options.LowThreshold >= 0 && options.LowThreshold < options.HighThreshold,
                "LoginRisk thresholds are invalid. Ensure LowThreshold >= 0 and LowThreshold < HighThreshold.")
            .Validate(
                options =>
                    options.NewDeviceWeight >= 0 &&
                    options.NewIpWeight >= 0 &&
                    options.GeoShiftWeight >= 0 &&
                    options.FailureBurstWeight >= 0,
                "LoginRisk weights must be non-negative.")
            .Validate(
                options => options.FailureBurstWindow > TimeSpan.Zero && options.HistoryWindow > TimeSpan.Zero,
                "LoginRisk windows must be positive.")
            .Validate(
                options => options.FailureBurstThreshold > 0,
                "LoginRisk failure burst threshold must be positive.")
            .Validate(
                options => options.TrustedDeviceDays > 0,
                "LoginRisk trusted device TTL must be positive.")
            .Validate(
                options => !options.GeoSignalTrusted || !string.IsNullOrWhiteSpace(options.HeaderCountryCodeName),
                "LoginRisk:HeaderCountryCodeName is required when GeoSignalTrusted is true.")
            .Validate(
                options => options.GeoSignalTrusted || options.GeoShiftWeight == 0,
                "LoginRisk:GeoShiftWeight must be zero until GeoSignalTrusted is true.")
            .ValidateOnStart();

        services.AddOptions<LoginRiskAlertingOptions>()
            .Bind(configuration.GetSection("LoginRisk:Alerting"))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.TemplateName),
                "LoginRisk:Alerting:TemplateName is required when alerting is enabled.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Subject),
                "LoginRisk:Alerting:Subject is required when alerting is enabled.")
            .ValidateOnStart();

        services.AddOptions<PasswordResetOptions>()
            .Bind(configuration.GetSection("PasswordReset"))
            .Validate(
                options => options.TokenTtlMinutes > 0 && options.TokenTtlMinutes <= 120,
                "PasswordReset:TokenTtlMinutes must be between 1 and 120.")
            .Validate(
                options => options.RequestLimitPerWindow > 0 && options.RequestLimitPerWindow <= 100,
                "PasswordReset:RequestLimitPerWindow must be between 1 and 100.")
            .Validate(
                options => options.RequestLimitWindowSeconds >= 30 && options.RequestLimitWindowSeconds <= 3600,
                "PasswordReset:RequestLimitWindowSeconds must be between 30 and 3600.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.TemplateName),
                "PasswordReset:TemplateName is required when password reset is enabled.")
            .Validate(
                options => !options.Enabled || options.AllowedBaseUrls.Count > 0,
                "PasswordReset:AllowedBaseUrls must contain at least one allowed https base URL when password reset is enabled.")
            .Validate(
                options => options.AllowedBaseUrls.All(IdentityHostModule.IsValidPublicBaseUrl),
                "PasswordReset:AllowedBaseUrls contains invalid URL(s).")
            .ValidateOnStart();

        services.AddOptions<EmailVerificationOptions>()
            .Bind(configuration.GetSection("EmailVerification"))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.TemplateName),
                "EmailVerification:TemplateName is required when email verification is enabled.")
            .Validate(
                options => !options.Enabled || options.AllowedBaseUrls.Count > 0,
                "EmailVerification:AllowedBaseUrls must contain at least one allowed https base URL when email verification is enabled.")
            .Validate(
                options => options.AllowedBaseUrls.All(IdentityHostModule.IsValidPublicBaseUrl),
                "EmailVerification:AllowedBaseUrls contains invalid URL(s).")
            .ValidateOnStart();

        services.AddOptions<PublicInvitationManagementOptions>()
            .Bind(configuration.GetSection("PublicInvitationManagement"))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.TemplateName),
                "PublicInvitationManagement:TemplateName is required when invitation management is enabled.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Subject),
                "PublicInvitationManagement:Subject is required when invitation management is enabled.")
            .Validate(
                options => options.TokenTtlHours > 0 && options.TokenTtlHours <= 24 * 30,
                "PublicInvitationManagement:TokenTtlHours must be between 1 and 720.")
            .Validate(
                options => !options.Enabled || options.AllowedBaseUrls.Count > 0,
                "PublicInvitationManagement:AllowedBaseUrls must contain at least one allowed public base URL when invitation management is enabled.")
            .Validate(
                options => options.AllowedBaseUrls.All(IdentityHostModule.IsValidPublicBaseUrl),
                "PublicInvitationManagement:AllowedBaseUrls contains invalid URL(s).")
            .ValidateOnStart();

        services.AddOptions<InteractiveLoginOptions>()
            .Bind(configuration.GetSection("InteractiveLogin"))
            .Validate(
                options => options.ChallengeTtlSeconds >= 30 && options.ChallengeTtlSeconds <= 900,
                "InteractiveLogin:ChallengeTtlSeconds must be between 30 and 900.")
            .ValidateOnStart();

        services.AddOptions<LoginAttemptProtectionOptions>()
            .Bind(configuration.GetSection("LoginAttemptProtection"))
            .Validate(
                options => options.RequestLimitPerWindow > 0 && options.RequestLimitPerWindow <= 500,
                "LoginAttemptProtection:RequestLimitPerWindow must be between 1 and 500.")
            .Validate(
                options => options.RequestLimitWindowSeconds >= 5 && options.RequestLimitWindowSeconds <= 3600,
                "LoginAttemptProtection:RequestLimitWindowSeconds must be between 5 and 3600.")
            .ValidateOnStart();

        var passwordResetTokenTtlMinutes = configuration.GetValue<int?>("PasswordReset:TokenTtlMinutes") ?? 15;
        services.Configure<DataProtectionTokenProviderOptions>(options =>
        {
            options.TokenLifespan = TimeSpan.FromMinutes(Math.Clamp(passwordResetTokenTtlMinutes, 1, 120));
        });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddHttpContextAccessor();
        var identityLoginPermitLimit = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Login:PermitLimit") ?? 120, 1, 1000);
        var identityLoginWindowSeconds = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Login:WindowSeconds") ?? 60, 5, 3600);
        var identityMfaPermitLimit = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Mfa:PermitLimit") ?? 90, 1, 1000);
        var identityMfaWindowSeconds = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Mfa:WindowSeconds") ?? 60, 5, 3600);
        var identityPasskeyPermitLimit = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Passkeys:PermitLimit") ?? 20, 1, 100);
        var identityPasskeyWindowSeconds = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Passkeys:WindowSeconds") ?? 60, 5, 3600);
        var identityResetPermitLimit = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Reset:PermitLimit") ?? 60, 1, 1000);
        var identityResetWindowSeconds = Math.Clamp(configuration.GetValue<int?>("IdentityRateLimiting:Reset:WindowSeconds") ?? 300, 5, 3600);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("identity-auth-login", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: IdentityHostModule.BuildIdentityPartitionKey(httpContext, "identity-auth-login"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = identityLoginPermitLimit,
                        Window = TimeSpan.FromSeconds(identityLoginWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

            options.AddPolicy("identity-auth-mfa", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: IdentityHostModule.BuildIdentityPartitionKey(httpContext, "identity-auth-mfa"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = identityMfaPermitLimit,
                        Window = TimeSpan.FromSeconds(identityMfaWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

            options.AddPolicy("identity-passkeys", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: IdentityHostModule.BuildAuthenticatedIdentityPartitionKey(
                        httpContext,
                        "identity-passkeys"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = identityPasskeyPermitLimit,
                        Window = TimeSpan.FromSeconds(identityPasskeyWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

            options.AddPolicy("identity-auth-reset", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: IdentityHostModule.BuildIdentityPartitionKey(httpContext, "identity-auth-reset"),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = identityResetPermitLimit,
                        Window = TimeSpan.FromSeconds(identityResetWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));
        });

        services.AddOpenIddict()
            .AddValidation(options =>
            {
                var issuer = configuration["AuthServer:Authority"] ?? (isDevelopment ? "http://localhost:5234/" : null);
                if (string.IsNullOrWhiteSpace(issuer))
                {
                    throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-identity",
                        key: "AuthServer:Authority",
                        rule: "Authority is required to configure OpenIddict validation outside Development.",
                        fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-identity/values.yaml and redeploy.");
                }
                var audiences = configuration["AuthServer:Audiences"] ?? "identity";
                var audienceList = audiences.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                options.SetIssuer(new Uri(issuer));
                options.AddAudiences(audienceList);
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        services.AddAuthorization(options =>
        {
            var allowedInternalClients = (configuration["IdentityInternal:AllowedClientIds"]
                ?? configuration["OpenIddict:Clients:Internal:ClientId"]
                ?? "auth-internal")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

            options.AddPolicy("IdentityInternal", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var scope = ctx.User.FindFirst(OpenIddictConstants.Claims.Scope)?.Value;
                    if (string.IsNullOrWhiteSpace(scope))
                    {
                        return false;
                    }

                    var hasScope = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("identity-internal", StringComparer.Ordinal);
                    if (!hasScope)
                    {
                        return false;
                    }

                    var clientId = ctx.User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
                    var subject = ctx.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                    if (string.IsNullOrWhiteSpace(clientId) || !allowedInternalClients.Contains(clientId))
                    {
                        return false;
                    }

                    return string.Equals(subject, clientId, StringComparison.Ordinal);
                });
            });

            options.AddPolicy("IdentitySelfOrInternal", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var scope = ctx.User.FindFirst(OpenIddictConstants.Claims.Scope)?.Value;
                    var clientId = ctx.User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
                    var subject = ctx.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                    var isInternal = !string.IsNullOrWhiteSpace(scope) &&
                                     scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                         .Contains("identity-internal", StringComparer.Ordinal) &&
                                     !string.IsNullOrWhiteSpace(clientId) &&
                                     allowedInternalClients.Contains(clientId) &&
                                     string.Equals(subject, clientId, StringComparison.Ordinal);
                    if (isInternal)
                    {
                        return true;
                    }

                    if (ctx.Resource is not HttpContext httpContext ||
                        !Guid.TryParse(subject, out var subjectId) ||
                        !Guid.TryParse(httpContext.Request.Query["userId"].ToString(), out var requestedUserId))
                    {
                        return false;
                    }

                    return subjectId == requestedUserId;
                });
            });
        });

        services.Replace(ServiceDescriptor.Singleton<IOptions<IdentityOptions>, HostIdentityOptionsAccessor>());
        services.Replace(ServiceDescriptor.Singleton<IOptions<LoginRiskOptions>, HostLoginRiskOptionsAccessor>());
        services.Replace(ServiceDescriptor.Singleton<IOptions<InteractiveLoginOptions>, HostInteractiveLoginOptionsAccessor>());
        services.Replace(ServiceDescriptor.Singleton<IOptions<LoginAttemptProtectionOptions>, HostLoginAttemptProtectionOptionsAccessor>());
        services.Replace(ServiceDescriptor.Singleton<IOptions<PasswordResetOptions>, HostPasswordResetOptionsAccessor>());
        services.Replace(ServiceDescriptor.Singleton<IOptions<EmailVerificationOptions>, HostEmailVerificationOptionsAccessor>());
        services.Replace(ServiceDescriptor.Singleton<IOptions<PublicInvitationManagementOptions>, HostPublicInvitationManagementOptionsAccessor>());

        services.Configure<SharedDataSeedOptions>(options =>
        {
            options.Contributors.Add<Identity.Data.DefaultIdentityDataSeedContributor>();
        });

        services.AddDataProtection()
            .SetApplicationName(configuration["DataProtection:ApplicationName"] ?? "TenantWright.Identity");
        services.TryAddTransient<IIdentityUserImportPayloadProtector, IdentityUserImportPayloadProtector>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(serviceProvider =>
            new ConfigureOptions<KeyManagementOptions>(options =>
            {
                var multiplexer = serviceProvider.GetService<IConnectionMultiplexer>();
                if (multiplexer == null)
                {
                    if (!isDevelopment)
                    {
                        throw new InvalidOperationException(
                            "Identity Data Protection requires Valkey outside Development.");
                    }

                    return;
                }

                var valkeyKey = configuration["DataProtection:ValkeyKey"]
                    ?? "DataProtection-Keys:Identity";
                options.XmlRepository = new RedisXmlRepository(
                    () => multiplexer.GetDatabase(),
                    valkeyKey);
            }));
    }

    private static bool IsValidPasskeyOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/");
    }

    internal static bool IsProductionPasskeyConfigurationValid(PasskeyOptions options)
    {
        if (!options.Enabled)
        {
            return true;
        }

        var relyingPartyId = options.RelyingPartyId.Trim();
        if (string.Equals(relyingPartyId, "localhost", StringComparison.OrdinalIgnoreCase) ||
            Uri.CheckHostName(relyingPartyId) != UriHostNameType.Dns)
        {
            return false;
        }

        return options.Origins.Count > 0 &&
               options.Origins.All(origin =>
                   Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                   string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   string.IsNullOrEmpty(uri.UserInfo) &&
                   string.IsNullOrEmpty(uri.Query) &&
                   string.IsNullOrEmpty(uri.Fragment) &&
                   (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/") &&
                   !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(uri.Host, relyingPartyId, StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith($".{relyingPartyId}", StringComparison.OrdinalIgnoreCase)));
    }
}
