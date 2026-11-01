using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.OpenApi;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class SharedHostAuthorizationTests
{
    [Fact]
    public void ProductionCredentialValidator_rejects_placeholder_credentials_outside_development()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=TenantWright;Username=postgres;Password=1234",
                ["Messaging:RabbitMq:Password"] = "CHANGE_ME"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ProductionCredentialValidator.Validate(configuration, isDevelopment: false, "tenantwright-test"));

        Assert.Contains("ConnectionStrings:Default", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCredentialValidator_allows_non_placeholder_password_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=db;Database=TenantWright;Username=app;Password=s3cret-value",
                ["Messaging:RabbitMq:Password"] = "rabbit-secret"
            })
            .Build();

        ProductionCredentialValidator.Validate(configuration, isDevelopment: false, "tenantwright-test");
    }

    [Fact]
    public void ProductionCredentialValidator_accepts_matching_transaction_pool_configuration()
    {
        var configuration = CreateExplicitPostgreSqlConfiguration(
            connectionString:
                "Host=pool.example;Port=6432;Database=TenantWright;Username=app;Password=s3cret-value;" +
                "Pooling=true;Maximum Pool Size=4;Minimum Pool Size=0;Connection Idle Lifetime=60;" +
                "Connection Pruning Interval=10;Connection Lifetime=300;Timeout=15;Command Timeout=30",
            mode: "TransactionPool");

        ProductionCredentialValidator.Validate(configuration, isDevelopment: false, "tenantwright-test");
    }

    [Fact]
    public void ProductionCredentialValidator_rejects_client_pool_budget_mismatch()
    {
        var configuration = CreateExplicitPostgreSqlConfiguration(
            connectionString:
                "Host=pool.example;Port=6432;Database=TenantWright;Username=app;Password=s3cret-value;" +
                "Pooling=true;Maximum Pool Size=20;Minimum Pool Size=0;Connection Idle Lifetime=60;" +
                "Connection Pruning Interval=10;Connection Lifetime=300;Timeout=15;Command Timeout=30",
            mode: "TransactionPool");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionCredentialValidator.Validate(configuration, isDevelopment: false, "tenantwright-test"));

        Assert.Contains("MaximumPoolSize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCredentialValidator_rejects_runtime_migrations_through_transaction_pool()
    {
        var configuration = CreateExplicitPostgreSqlConfiguration(
            connectionString:
                "Host=pool.example;Port=6432;Database=TenantWright;Username=app;Password=s3cret-value;" +
                "Pooling=true;Maximum Pool Size=4;Minimum Pool Size=0;Connection Idle Lifetime=60;" +
                "Connection Pruning Interval=10;Connection Lifetime=300;Timeout=15;Command Timeout=30",
            mode: "TransactionPool",
            autoMigrate: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionCredentialValidator.Validate(configuration, isDevelopment: false, "tenantwright-test"));

        Assert.Contains("EfCore:AutoMigrate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_authorization_defaults_require_authenticated_fallback()
    {
        var options = new AuthorizationOptions();

        SharedAuthorizationDefaults.Configure(options);

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void Production_openapi_endpoint_requires_explicit_authorization()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenApi:Enabled"] = "true",
            ["OpenApi:DevelopmentOnly"] = "false"
        });
        builder.Services.AddSharedOpenApi(builder.Configuration, builder.Environment);
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.MapSharedOpenApi();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .ToArray();
        var openApiEndpoint = Assert.Single(endpoints, endpoint =>
            endpoint is RouteEndpoint routeEndpoint
            && string.Equals(routeEndpoint.RoutePattern.RawText, "/swagger/{documentName}/swagger.json", StringComparison.Ordinal));

        Assert.Contains(openApiEndpoint.Metadata, metadata => metadata is IAuthorizeData);
        Assert.DoesNotContain(openApiEndpoint.Metadata, metadata => metadata is IAllowAnonymous);
    }

    [Fact]
    public void Shared_problem_details_contract_exposes_stable_machine_readable_fields()
    {
        var properties = typeof(Shared.AspNetCore.ProblemDetails.SharedHttpProblemDetails)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("Type", properties);
        Assert.Contains("Title", properties);
        Assert.Contains("Status", properties);
        Assert.Contains("Detail", properties);
        Assert.Contains("Code", properties);
        Assert.Contains("TraceId", properties);
        Assert.Contains("Errors", properties);
    }

    private static IConfiguration CreateExplicitPostgreSqlConfiguration(
        string connectionString,
        string mode,
        bool autoMigrate = false)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["EfCore:AutoMigrate"] = autoMigrate.ToString(),
                ["PostgreSql:Runtime:RequireExplicitConfiguration"] = "true",
                ["PostgreSql:Runtime:Mode"] = mode,
                ["PostgreSql:Runtime:BackendPoolSize"] = mode == "Direct" ? "0" : "4",
                ["PostgreSql:Runtime:ClientPool:Pooling"] = "true",
                ["PostgreSql:Runtime:ClientPool:MaximumPoolSize"] = "4",
                ["PostgreSql:Runtime:ClientPool:MinimumPoolSize"] = "0",
                ["PostgreSql:Runtime:ClientPool:ConnectionIdleLifetimeSeconds"] = "60",
                ["PostgreSql:Runtime:ClientPool:ConnectionPruningIntervalSeconds"] = "10",
                ["PostgreSql:Runtime:ClientPool:ConnectionLifetimeSeconds"] = "300",
                ["PostgreSql:Runtime:ClientPool:ConnectionTimeoutSeconds"] = "15",
                ["PostgreSql:Runtime:ClientPool:CommandTimeoutSeconds"] = "30"
            })
            .Build();
    }

}
