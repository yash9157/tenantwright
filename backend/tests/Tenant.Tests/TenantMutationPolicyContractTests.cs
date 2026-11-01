using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Controllers.Tenants;
using Xunit;

namespace Tenant.Tests;

/// <summary>
/// Contract tests asserting each Tenant mutation endpoint enforces its specific
/// child permission policy AND is audited (design §6, Permission Matrix; R5, R18.4,
/// R19.4, R20.6). These are reflection contract tests: they lock the controller's
/// authorization + audit surface without spinning up the full pipeline.
/// </summary>
public class TenantMutationPolicyContractTests
{
    [Fact]
    public void Base_controller_access_is_gated_by_the_parent_tenant_management_policy()
    {
        var authorize = typeof(TenantsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(TenantPermissions.Management.Default, authorize.Policy);
    }

    [Fact]
    public void Tenant_selection_options_should_be_a_bounded_management_read()
    {
        var method = typeof(TenantsController)
            .GetMethod(nameof(TenantsController.GetSelectionOptionsAsync));
        var route = method?.GetCustomAttribute<HttpGetAttribute>();

        Assert.Equal("selection-options", route?.Template);
        Assert.Null(method?.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(nameof(TenantsController.CreateAsync), "Tenant.Management.Create")]
    [InlineData(nameof(TenantsController.UpdateAsync), "Tenant.Management.Update")]
    [InlineData(nameof(TenantsController.RenameAsync), "Tenant.Management.Update")]
    [InlineData(nameof(TenantsController.DeleteAsync), "Tenant.Management.Delete")]
    [InlineData(nameof(TenantsController.ArchiveAsync), "Tenant.Management.Archive")]
    [InlineData(nameof(TenantsController.ArchiveOverrideAsync), "Tenant.BreakGlass.ArchiveOverride")]
    [InlineData(nameof(TenantsController.TransitionLifecycleAsync), "Tenant.Management.Lifecycle.Manage")]
    [InlineData(nameof(TenantsController.RestoreAsync), "Tenant.Management.Restore")]
    [InlineData(nameof(TenantsController.RestoreDetailedAsync), "Tenant.Management.Restore")]
    [InlineData(nameof(TenantsController.ResendOwnershipRequestAsync), "Tenant.Management.Ownership.Transfer")]
    public void Mutation_endpoints_enforce_their_child_policy(string methodName, string expectedPolicy)
    {
        var method = GetMethod(methodName);

        var authorize = method
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.NotNull(authorize);
        Assert.Equal(expectedPolicy, authorize!.Policy);
    }

    [Theory]
    [InlineData(nameof(TenantsController.CreateAsync))]
    [InlineData(nameof(TenantsController.UpdateAsync))]
    [InlineData(nameof(TenantsController.RenameAsync))]
    [InlineData(nameof(TenantsController.DeleteAsync))]
    [InlineData(nameof(TenantsController.ArchiveAsync))]
    [InlineData(nameof(TenantsController.ArchiveOverrideAsync))]
    [InlineData(nameof(TenantsController.TransitionLifecycleAsync))]
    [InlineData(nameof(TenantsController.RestoreAsync))]
    [InlineData(nameof(TenantsController.RestoreDetailedAsync))]
    [InlineData(nameof(TenantsController.ResendOwnershipRequestAsync))]
    public void Mutation_endpoints_are_audited(string methodName)
    {
        var method = GetMethod(methodName);

        var hasAudited = method
            .GetCustomAttributes(inherit: true)
            .Any(a => a.GetType().Name == "AuditedAttribute");

        Assert.True(hasAudited, $"{methodName} must be marked [Audited] (sensitive operation, R5).");
    }

    [Fact]
    public void Mutation_policy_constants_are_distinct_and_break_glass_is_isolated()
    {
        var managementChildren = new[]
        {
            TenantPermissions.Management.Create,
            TenantPermissions.Management.Update,
            TenantPermissions.Management.Delete,
            TenantPermissions.Management.Archive,
        };

        Assert.Equal(managementChildren.Length, managementChildren.Distinct().Count());
        Assert.All(
            managementChildren,
            child => Assert.StartsWith(
                TenantPermissions.Management.Default + ".",
                child,
                StringComparison.Ordinal));
        Assert.StartsWith(TenantPermissions.Management.Default + ".", TenantPermissions.Lifecycle.Manage, StringComparison.Ordinal);
        Assert.StartsWith(TenantPermissions.BreakGlass.Default + ".", TenantPermissions.BreakGlass.ArchiveOverride, StringComparison.Ordinal);
        Assert.False(TenantPermissions.BreakGlass.ArchiveOverride.StartsWith(TenantPermissions.Management.Default + ".", StringComparison.Ordinal));
        Assert.DoesNotContain(TenantPermissions.BreakGlass.ArchiveOverride, managementChildren);
    }

    private static MethodInfo GetMethod(string name)
    {
        var method = typeof(TenantsController).GetMethod(name);
        Assert.NotNull(method);
        return method!;
    }
}
