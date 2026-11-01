using Identity.Application;
using Identity.Domain;
using Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.BackgroundTasks.Hangfire;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;
using Shared.Uow.Implementations;
using Shared.EventBus.Hosting;
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
using Permissions.Application.Contracts.Definitions;
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
using System.Threading.RateLimiting;
using Identity.Security;
using Identity.Application.Users;
using Shared.EventBus.Messaging;
using Shared.Permissions.Definitions.Tenant;

namespace Identity;

[DependsOn(typeof(IdentityDomainModule))]
[DependsOn(typeof(IdentityApplicationModule))]
[DependsOn(typeof(IdentityEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(SharedBackgroundTasksHangfireModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class IdentityHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.AddIdentityHostServices();
        RegisterExternalPermissionDefinitionProviders(context.Services);

        var configuration = context.Configuration;
        context.Services.AddTenantWrightMessagingHost<global::Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp>(
            configuration,
            TenantWrightMessagingTopology.IdentityPrincipal);
        var sessionValidationOptions = configuration
            .GetSection("SessionValidation")
            .Get<Shared.Caching.Sessions.SessionValidationOptions>() ?? new Shared.Caching.Sessions.SessionValidationOptions();

        var cleanupWorkerOptions = Identity.Workers.IdentitySessionCleanupWorkerOptions.FromSessionValidation(sessionValidationOptions);

        context.Services.AddSharedPeriodicWorker<Identity.Workers.IdentitySessionCleanupWorker, Identity.Workers.IdentitySessionCleanupWorkerOptions>(
            options =>
            {
                options.Enabled = cleanupWorkerOptions.Enabled;
                options.WorkerName = cleanupWorkerOptions.WorkerName;
                options.PollIntervalMs = cleanupWorkerOptions.PollIntervalMs;
                options.ErrorDelayMs = cleanupWorkerOptions.ErrorDelayMs;
                options.DistributedLockName = cleanupWorkerOptions.DistributedLockName;
                options.OwnershipAcquireTimeoutMs = cleanupWorkerOptions.OwnershipAcquireTimeoutMs;
                options.OwnershipLeaseMs = cleanupWorkerOptions.OwnershipLeaseMs;
            });
    }

    internal static void RegisterExternalPermissionDefinitionProviders(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<PermissionDefinitionProvider, TenantPermissionDefinitionProvider>());
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var app = context.ApplicationBuilder;
        var env = context.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
            await seeder.SeedAsync();
        }

        if (SharedAspNetCoreHostModule.IsHttpsRedirectionEnabled(configuration))
        {
            app.UseHttpsRedirection();
        }
        app.UseRouting();
        app.UseUnitOfWork();
        app.UseAuditLogCapture();
        app.UseAuthentication();
        app.UseMiddleware<SessionValidationMiddleware>();
        app.UseMiddleware<MultiTenancyMiddleware>();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }

    internal static bool IsValidPublicBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildIdentityPartitionKey(HttpContext httpContext, string policyName)
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

    internal static string BuildAuthenticatedIdentityPartitionKey(
        HttpContext httpContext,
        string policyName)
    {
        var subject = httpContext.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            subject = "missing-subject";
        }

        return $"{BuildIdentityPartitionKey(httpContext, policyName)}:{subject}";
    }

    internal static HostIdentitySecurityStaticOptions BuildHostIdentitySecurityStaticOptions(
        IConfiguration configuration,
        bool isDevelopment,
        bool lockoutAllowedForNewUsers,
        int lockoutMaxFailedAccessAttempts,
        int lockoutMinutes)
    {
        var loginRiskOptions = new LoginRiskOptions();
        configuration.GetSection("LoginRisk").Bind(loginRiskOptions);

        var passwordResetOptions = new PasswordResetOptions();
        configuration.GetSection("PasswordReset").Bind(passwordResetOptions);

        var emailVerificationOptions = new EmailVerificationOptions();
        configuration.GetSection("EmailVerification").Bind(emailVerificationOptions);

        var publicInvitationOptions = new PublicInvitationManagementOptions();
        configuration.GetSection("PublicInvitationManagement").Bind(publicInvitationOptions);

        var interactiveLoginOptions = new InteractiveLoginOptions();
        configuration.GetSection("InteractiveLogin").Bind(interactiveLoginOptions);

        var loginAttemptProtectionOptions = new LoginAttemptProtectionOptions();
        configuration.GetSection("LoginAttemptProtection").Bind(loginAttemptProtectionOptions);

        return new HostIdentitySecurityStaticOptions
        {
            DefaultSettings = new HostIdentitySecuritySettingsDto
            {
                ConcurrencyStamp = HostIdentitySecurityPolicy.SingletonId.ToString("N"),
                Password = new HostIdentitySecurityPasswordSettingsDto
                {
                    RequiredLength = isDevelopment ? 6 : 8,
                    RequireDigit = !isDevelopment,
                    RequireLowercase = !isDevelopment,
                    RequireUppercase = !isDevelopment,
                    RequireNonAlphanumeric = !isDevelopment
                },
                Lockout = new HostIdentitySecurityLockoutSettingsDto
                {
                    AllowedForNewUsers = lockoutAllowedForNewUsers,
                    MaxFailedAccessAttempts = lockoutMaxFailedAccessAttempts,
                    DefaultLockoutMinutes = lockoutMinutes
                },
                LoginRisk = new HostIdentityLoginRiskSettingsDto
                {
                    Enabled = loginRiskOptions.Enabled,
                    Mode = loginRiskOptions.Mode,
                    LowThreshold = loginRiskOptions.LowThreshold,
                    HighThreshold = loginRiskOptions.HighThreshold,
                    NewDeviceWeight = loginRiskOptions.NewDeviceWeight,
                    NewIpWeight = loginRiskOptions.NewIpWeight,
                    GeoShiftWeight = loginRiskOptions.GeoShiftWeight,
                    FailureBurstWeight = loginRiskOptions.FailureBurstWeight,
                    FailureBurstWindowMinutes = (int)Math.Max(1, loginRiskOptions.FailureBurstWindow.TotalMinutes),
                    FailureBurstThreshold = loginRiskOptions.FailureBurstThreshold,
                    HistoryWindowDays = (int)Math.Max(1, loginRiskOptions.HistoryWindow.TotalDays),
                    TrustedDeviceDays = loginRiskOptions.TrustedDeviceDays
                },
                InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
                {
                    ChallengeTtlSeconds = interactiveLoginOptions.ChallengeTtlSeconds
                },
                LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
                {
                    Enabled = loginAttemptProtectionOptions.Enabled,
                    RequestLimitPerWindow = loginAttemptProtectionOptions.RequestLimitPerWindow,
                    RequestLimitWindowSeconds = loginAttemptProtectionOptions.RequestLimitWindowSeconds
                },
                PasswordReset = new HostIdentityPasswordResetSettingsDto
                {
                    Enabled = passwordResetOptions.Enabled,
                    RequestLimitPerWindow = passwordResetOptions.RequestLimitPerWindow,
                    RequestLimitWindowSeconds = passwordResetOptions.RequestLimitWindowSeconds
                },
                EmailVerification = new HostIdentityEmailVerificationSettingsDto
                {
                    Enabled = emailVerificationOptions.Enabled
                },
                PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
                {
                    Enabled = publicInvitationOptions.Enabled,
                    TokenTtlHours = publicInvitationOptions.TokenTtlHours
                }
            },
            LoginRiskHeaderCountryCodeName = loginRiskOptions.HeaderCountryCodeName,
            LoginRiskGeoSignalTrusted = loginRiskOptions.GeoSignalTrusted,
            PasswordResetTokenTtlMinutes = passwordResetOptions.TokenTtlMinutes,
            PasswordResetTemplateName = passwordResetOptions.TemplateName,
            PasswordResetAllowedBaseUrls = [.. passwordResetOptions.AllowedBaseUrls],
            EmailVerificationTemplateName = emailVerificationOptions.TemplateName,
            EmailVerificationAllowedBaseUrls = [.. emailVerificationOptions.AllowedBaseUrls],
            PublicInvitationTemplateName = publicInvitationOptions.TemplateName,
            PublicInvitationSubject = publicInvitationOptions.Subject,
            PublicInvitationAllowedBaseUrls = [.. publicInvitationOptions.AllowedBaseUrls]
        };
    }
}
