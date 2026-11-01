using Identity.Application.Contracts.Users;
using Identity.Controllers.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Permissions.Application.Contracts.Definitions;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Tenant;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Contract tests for the tenant primary-admin summary endpoint (design §8,
/// Option A). They assert the host-safe authorization surface and the batch
/// shape that removes the per-tenant N+1 the console previously incurred. The
/// single set-based query itself is covered in <c>UserAppService</c> (internal),
/// and documented there.
/// </summary>
public class TenantPrimaryAdminSummaryContractTests
{
    [Fact]
    public void Users_controller_is_gated_by_the_host_user_management_policy()
    {
        var authorize = typeof(UsersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(IdentityPermissions.Users.Default, authorize.Policy);
    }

    [Fact]
    public void Primary_admins_controller_is_gated_by_tenant_management()
    {
        var authorize = typeof(TenantPrimaryAdminsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(TenantPermissions.Management.Default, authorize.Policy);
    }

    [Fact]
    public void Identity_host_registers_the_tenant_definition_provider_for_its_cross_service_policy()
    {
        var services = new ServiceCollection();

        IdentityHostModule.RegisterExternalPermissionDefinitionProviders(services);

        using var serviceProvider = services.BuildServiceProvider();
        var providers = serviceProvider.GetServices<PermissionDefinitionProvider>();
        Assert.Contains(providers, provider => provider is TenantPermissionDefinitionProvider);
    }

    [Fact]
    public void Primary_admins_endpoint_is_a_batch_post_returning_the_admin_projection()
    {
        var method = typeof(TenantPrimaryAdminsController).GetMethod(nameof(TenantPrimaryAdminsController.GetAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<List<TenantPrimaryAdminDto>>), method!.ReturnType);

        var post = method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true)
            .Cast<HttpPostAttribute>()
            .Single();
        Assert.Null(post.Template);

        // It must not weaken or bypass the controller-level host policy.
        Assert.Empty(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public void Input_is_a_batch_of_tenant_ids_not_a_single_tenant()
    {
        var tenantIds = typeof(TenantPrimaryAdminsInput).GetProperty(nameof(TenantPrimaryAdminsInput.TenantIds));

        Assert.NotNull(tenantIds);
        Assert.Equal(typeof(List<Guid>), tenantIds!.PropertyType);
    }

    [Fact]
    public void Result_is_the_admin_projection_keyed_by_tenant()
    {
        var propertyNames = typeof(TenantPrimaryAdminDto)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { "IsActive", "PrimaryAdminEmail", "PrimaryAdminName", "PrimaryAdminUserId", "TenantId" },
            propertyNames);
    }
}
