using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.CapabilityMetadata;
using Permissions.Application.Contracts.PermissionHistory;
using Permissions.Controllers.AccessReviews;
using Permissions.Controllers.CapabilityMetadata;
using Permissions.Controllers.FeatureManagement;
using Permissions.Controllers.PermissionApproval;
using Permissions.Controllers.PermissionCatalog;
using Permissions.Controllers.PermissionConflictRules;
using Permissions.Controllers.PermissionHistory;
using Permissions.Controllers.TenantPermissionAccess;
using Permissions.Tests.TestDoubles;
using Xunit;

namespace Permissions.Tests;

public sealed class PlatformAccessControllersTests
{
    [Fact]
    public void Platform_access_endpoints_require_the_approved_host_roles_and_history_permission()
    {
        var capabilityAuthorize = GetAuthorizeAttribute<PlatformCapabilityMetadataController>(
            nameof(PlatformCapabilityMetadataController.GetAsync));
        var historyAuthorize = GetAuthorizeAttribute<PlatformPermissionGrantHistoryController>(
            nameof(PlatformPermissionGrantHistoryController.GetListAsync));

        Assert.Equal(PermissionsPolicies.ReadGrantHistory, capabilityAuthorize.Policy);
        Assert.Equal(PermissionsPolicies.ReadGrantHistory, historyAuthorize.Policy);
        Assert.Equal("admin,HostAdmin", capabilityAuthorize.Roles);
        Assert.Equal("admin,HostAdmin", historyAuthorize.Roles);

        var internalPolicies = typeof(InternalCapabilityMetadataController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .ToArray();
        Assert.Equal(PermissionsPolicies.ReadCapabilityMetadataInternal, Assert.Single(internalPolicies));
    }

    [Theory]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.GetListAsync), PermissionsPolicies.ReadGrantApprovals)]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.GetMineAsync), PermissionsPolicies.ManagePermissionGrants)]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.ApproveAsync), PermissionsPolicies.DecideGrantApprovals)]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.RejectAsync), PermissionsPolicies.DecideGrantApprovals)]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.CancelAsync), PermissionsPolicies.ManagePermissionGrants)]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.CancelAnyAsync), PermissionsPolicies.CancelAnyGrantApproval)]
    [InlineData(typeof(PermissionApprovalController), nameof(PermissionApprovalController.DecideBulkAsync), PermissionsPolicies.DecideGrantApprovals)]
    [InlineData(typeof(PermissionCatalogController), nameof(PermissionCatalogController.GetAsync), PermissionsPolicies.ReadPermissionCatalog)]
    [InlineData(typeof(PermissionCatalogController), nameof(PermissionCatalogController.CreateAsync), PermissionsPolicies.CreatePermissionCatalog)]
    [InlineData(typeof(PermissionCatalogController), nameof(PermissionCatalogController.UpdateAsync), PermissionsPolicies.UpdatePermissionCatalog)]
    [InlineData(typeof(PermissionCatalogController), nameof(PermissionCatalogController.DeleteAsync), PermissionsPolicies.DeletePermissionCatalog)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.GetRulesAsync), PermissionsPolicies.ReadPermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.GetImpactAsync), PermissionsPolicies.ReadPermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.GetChangeRequestsAsync), PermissionsPolicies.ReadPermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.RequestCreateAsync), PermissionsPolicies.CreatePermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.RequestUpdateAsync), PermissionsPolicies.UpdatePermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.RequestDeleteAsync), PermissionsPolicies.DeletePermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.ApproveAsync), PermissionsPolicies.DecidePermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.RejectAsync), PermissionsPolicies.DecidePermissionConflictRules)]
    [InlineData(typeof(PermissionConflictRulesController), nameof(PermissionConflictRulesController.CancelAsync), PermissionsPolicies.ReadPermissionConflictRules)]
    [InlineData(typeof(FeatureManagementController), nameof(FeatureManagementController.GetAsync), PermissionsPolicies.ReadFeatures)]
    [InlineData(typeof(FeatureManagementController), nameof(FeatureManagementController.UpdateAsync), PermissionsPolicies.UpdateFeatures)]
    public void Management_actions_require_their_exact_policy(Type controllerType, string methodName, string expectedPolicy)
    {
        var authorize = GetAuthorizeAttribute(controllerType, methodName);

        Assert.Equal(expectedPolicy, authorize.Policy);
    }

    [Fact]
    public void Access_review_routes_require_the_dedicated_review_policy()
    {
        var policies = typeof(AccessReviewController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToArray();

        Assert.Collection(policies, policy => Assert.Equal(PermissionsPolicies.ManageAccessReviews, policy));
    }

    [Fact]
    public void Host_tenant_delegation_route_requires_host_role_and_grant_management_policy()
    {
        var authorize = typeof(HostTenantDelegationController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal("admin,HostAdmin", authorize.Roles);
        Assert.Equal(PermissionsPolicies.ManagePermissionGrants, authorize.Policy);
    }

    [Fact]
    public async Task Tenant_context_is_forbidden_from_platform_access_endpoints()
    {
        var tenant = new TestCurrentTenant(Guid.NewGuid());
        var historyService = new RecordingHistoryService();
        var capabilityService = new RecordingCapabilityService();
        var historyController = new PlatformPermissionGrantHistoryController(historyService, tenant);
        var capabilityController = new PlatformCapabilityMetadataController(capabilityService, tenant);

        var historyResult = await historyController.GetListAsync(
            new GetPermissionGrantHistoryInput(),
            CancellationToken.None);
        var capabilityResult = await capabilityController.GetAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(historyResult.Result);
        Assert.IsType<ForbidResult>(capabilityResult.Result);
        Assert.False(historyService.PlatformQueryCalled);
        Assert.False(capabilityService.QueryCalled);
    }

    [Fact]
    public async Task Host_context_can_reach_the_platform_history_query()
    {
        var service = new RecordingHistoryService();
        var controller = new PlatformPermissionGrantHistoryController(service, new TestCurrentTenant(null));

        var result = await controller.GetListAsync(new GetPermissionGrantHistoryInput(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(service.PlatformQueryCalled);
    }

    private static AuthorizeAttribute GetAuthorizeAttribute<TController>(string methodName)
    {
        return GetAuthorizeAttribute(typeof(TController), methodName);
    }

    private static AuthorizeAttribute GetAuthorizeAttribute(Type controllerType, string methodName)
    {
        return controllerType
            .GetMethod(methodName)!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
    }

    private sealed class RecordingHistoryService : IPermissionGrantHistoryAppService
    {
        public bool PlatformQueryCalled { get; private set; }

        public Task<PermissionGrantHistoryResultDto> GetListAsync(
            GetPermissionGrantHistoryInput input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PermissionGrantHistoryResultDto());
        }

        public Task<PermissionGrantHistoryResultDto> GetPlatformListAsync(
            GetPermissionGrantHistoryInput input,
            CancellationToken cancellationToken = default)
        {
            PlatformQueryCalled = true;
            return Task.FromResult(new PermissionGrantHistoryResultDto());
        }
    }

    private sealed class RecordingCapabilityService : ICapabilityMetadataAppService
    {
        public bool QueryCalled { get; private set; }

        public Task<CapabilityMetadataResultDto> GetAsync(CancellationToken cancellationToken = default)
        {
            QueryCalled = true;
            return Task.FromResult(new CapabilityMetadataResultDto());
        }

        public Task<ValidateFeatureCapabilitiesResultDto> ValidateFeaturesAsync(
            ValidateFeatureCapabilitiesRequestDto request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidateFeatureCapabilitiesResultDto());
    }
}
