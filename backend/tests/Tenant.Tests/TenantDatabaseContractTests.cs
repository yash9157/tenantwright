using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Tenants;
using Tenant.Controllers.Tenants;
using Xunit;

namespace Tenant.Tests;

/// <summary>
/// HTTP contract of the dedicated-database endpoints: per-name routes, child permission policies, auditing, and DTOs
/// that can never carry a connection string. The legacy Default-only routes must be gone.
/// </summary>
public class TenantDatabaseContractTests
{
    [Theory]
    [InlineData(nameof(TenantDatabasesController.GetDatabasesAsync), TenantPermissions.ConnectionStrings.Read)]
    [InlineData(nameof(TenantDatabasesController.TestDatabaseAsync), TenantPermissions.ConnectionStrings.Test)]
    [InlineData(nameof(TenantDatabasesController.SetDatabaseAsync), TenantPermissions.ConnectionStrings.Update)]
    [InlineData(nameof(TenantDatabasesController.RemoveDatabaseAsync), TenantPermissions.ConnectionStrings.Delete)]
    public void Endpoints_enforce_their_child_policy(string methodName, string expectedPolicy)
    {
        var authorize = GetMethod(methodName)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Single();

        Assert.Equal(expectedPolicy, authorize.Policy);
    }

    [Fact]
    public void Controller_requires_tenant_management_and_routes_every_database_by_name()
    {
        Assert.Equal(
            TenantPermissions.Management.Default,
            typeof(TenantDatabasesController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(
            "api/tenant/tenants/{id:guid}/databases",
            typeof(TenantDatabasesController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("{name}/test", GetMethod(nameof(TenantDatabasesController.TestDatabaseAsync))
            .GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal("{name}", GetMethod(nameof(TenantDatabasesController.SetDatabaseAsync))
            .GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.Equal("{name}", GetMethod(nameof(TenantDatabasesController.RemoveDatabaseAsync))
            .GetCustomAttribute<HttpDeleteAttribute>()!.Template);
    }

    [Theory]
    [InlineData(nameof(TenantDatabasesController.TestDatabaseAsync))]
    [InlineData(nameof(TenantDatabasesController.SetDatabaseAsync))]
    [InlineData(nameof(TenantDatabasesController.RemoveDatabaseAsync))]
    public void Sensitive_endpoints_are_audited(string methodName)
    {
        Assert.Contains(
            GetMethod(methodName).GetCustomAttributes(inherit: true),
            attribute => attribute.GetType().Name == "AuditedAttribute");
    }

    [Fact]
    public void No_tenant_action_returns_a_raw_string_and_the_default_only_routes_are_gone()
    {
        var actions = new[] { typeof(TenantsController), typeof(TenantDatabasesController) }
            .SelectMany(type => type.GetMethods().Where(method => method.DeclaringType == type))
            .ToArray();

        Assert.DoesNotContain(actions, method => method.ReturnType == typeof(Task<string>));
        Assert.DoesNotContain(actions, method => method.Name.Contains("DefaultConnectionString", StringComparison.Ordinal));
        var templates = actions
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
            .Select(attribute => attribute.Template ?? string.Empty);
        Assert.DoesNotContain(templates, template => template.Contains("default-connection-string", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(TenantDatabaseStatusDto), new[] { "Fingerprint", "IsDedicated", "Name" })]
    [InlineData(typeof(TenantDatabaseTestResultDto), new[] { "Message", "Success" })]
    [InlineData(typeof(SetTenantDatabaseResultDto), new[] { "Change", "DataMigrated", "Status", "TestResult" })]
    [InlineData(typeof(TenantDatabaseListDto), new[] { "ConcurrencyStamp", "Items" })]
    public void Response_dtos_never_carry_a_connection_string(Type dtoType, string[] expectedProperties)
    {
        var properties = dtoType.GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal(expectedProperties, properties);
    }

    private static MethodInfo GetMethod(string name) =>
        typeof(TenantDatabasesController).GetMethod(name)
        ?? throw new InvalidOperationException($"Missing action {name}.");
}
