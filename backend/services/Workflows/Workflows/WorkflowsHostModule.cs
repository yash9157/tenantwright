using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Validation.AspNetCore;
using Permissions.Application.Contracts.Features;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Permissions;
using Shared.Permissions.Features;
using Shared.Uow.Implementations;
using Shared.EventBus.Hosting;
using Workflows.Application;
using Workflows.EntityFrameworkCore;
using Shared.EventBus.Messaging;

namespace Workflows;

[DependsOn(typeof(WorkflowsApplicationModule))]
[DependsOn(typeof(WorkflowsEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class WorkflowsHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = context.Configuration;
        services.AddTenantWrightMessagingHost<global::Workflows.EntityFrameworkCore.EntityFrameworkCore.WorkflowsDbContextApp>(
            configuration,
            TenantWrightMessagingTopology.WorkflowsPrincipal);
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        services.Configure<FeatureCheckerClientOptions>(configuration.GetSection("RemoteFeatureChecker"));
        services.AddHttpClient(nameof(HttpFeatureCheckerClient));
        services.TryAddTransient<IFeatureCheckerClient, HttpFeatureCheckerClient>();

        ValidateFailClosedSettings(configuration, isDevelopment);

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        services.AddOpenIddict()
            .AddValidation(options =>
            {
                var issuer = configuration["AuthServer:Authority"] ?? (isDevelopment ? "http://localhost:5234/" : null);
                if (string.IsNullOrWhiteSpace(issuer))
                {
                    throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-workflows",
                        key: "AuthServer:Authority",
                        rule: "Authority is required to configure OpenIddict validation outside Development.",
                        fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-workflows/values.yaml and redeploy.");
                }

                var audiences = (configuration["AuthServer:Audiences"] ?? "workflows")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                options.SetIssuer(new Uri(issuer));
                options.AddAudiences(audiences);
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        services.AddAuthorization();

        services.Configure<SharedDataSeedOptions>(_ =>
        {
        });
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
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }

    private static void ValidateFailClosedSettings(IConfiguration configuration, bool isDevelopment)
    {
        if (isDevelopment)
        {
            return;
        }

        var authority = configuration["AuthServer:Authority"];
        if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-workflows",
                key: "AuthServer:Authority",
                rule: "Must be a non-empty https URL outside Development.",
                fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-workflows/values.yaml to an https URL and redeploy.");
        }

        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-workflows",
                key: "ConnectionStrings:Default",
                rule: "Default connection string is required outside Development.",
                fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-workflows-db.connection-string' in charts/tenantwright-workflows/values.yaml and ensure the secret exists.");
        }
    }
}
