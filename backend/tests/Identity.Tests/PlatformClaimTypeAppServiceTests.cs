using System.Reflection;
using Identity.Application.ClaimTypes;
using Identity.Application.Contracts.ClaimTypes;
using Identity.Controllers.ClaimTypes;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Identity;
using Xunit;

namespace Identity.Tests;

public sealed class PlatformClaimTypeAppServiceTests
{
    [Fact]
    public async Task Platform_list_is_bounded_to_the_explicit_tenant_and_restores_host_scope()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.IdentityClaimTypes.AddRange(
            CreateClaimType(tenantA, "tenant-a-code"),
            CreateClaimType(tenantB, "tenant-b-code"));
        await dbContext.SaveChangesAsync();
        var service = new IdentityClaimTypeAppService(IdentityTestSupport.AsDbContextProvider(dbContext), currentTenant);

        var result = await service.GetPlatformListAsync(tenantA, new ClaimTypeListInput());

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("tenant-a-code", Assert.Single(result.Items).Name);
        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public async Task Platform_operations_reject_ambient_tenant_context()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(Guid.NewGuid());
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var service = new IdentityClaimTypeAppService(IdentityTestSupport.AsDbContextProvider(dbContext), currentTenant);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetPlatformListAsync(Guid.NewGuid(), new ClaimTypeListInput()));

        Assert.Contains("host scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Platform_controller_uses_tenant_bounded_routes_and_exact_action_permissions()
    {
        var route = typeof(PlatformClaimTypesController)
            .GetCustomAttribute<RouteAttribute>();
        Assert.Equal("api/identity/platform/tenants/{tenantId:guid}/claim-types", route?.Template);
        Assert.Equal(
            IdentityPermissions.ClaimTypes.Default,
            typeof(PlatformClaimTypesController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);

        AssertPolicy(nameof(PlatformClaimTypesController.CreateAsync), IdentityPermissions.ClaimTypes.Create);
        AssertPolicy(nameof(PlatformClaimTypesController.UpdateAsync), IdentityPermissions.ClaimTypes.Update);
        AssertPolicy(nameof(PlatformClaimTypesController.DeleteAsync), IdentityPermissions.ClaimTypes.Delete);
    }

    private static IdentityClaimType CreateClaimType(Guid tenantId, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            ValueType = IdentityClaimValueType.String
        };

    private static void AssertPolicy(string methodName, string expectedPolicy)
    {
        var method = typeof(PlatformClaimTypesController).GetMethod(methodName);
        Assert.NotNull(method);
        Assert.Equal(expectedPolicy, method!.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }
}
