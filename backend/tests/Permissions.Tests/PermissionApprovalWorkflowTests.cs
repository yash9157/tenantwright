using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Permissions.Application.Caching;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Tests.TestDoubles;
using Shared;
using Shared.CurrentUser;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Flow 6 approval workflow: a sensitive grant becomes a Pending request; a different admin must
/// approve it to apply the grant; the requester cannot approve their own request.
/// </summary>
public class PermissionApprovalWorkflowTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string ProviderName = "User";
    private const string ProviderKey = "user-1";
    private const string Permission = "Permissions.Management.Grants";

    [Fact]
    public async Task Creating_a_request_does_not_write_a_grant()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (svc, ctx) = Build(connection, requester);

        await svc.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, "need access", "T-1", requester);
        await ctx.SaveChangesAsync();

        Assert.Empty(await ReadContext(connection).PermissionGrants.ToListAsync());
        var request = Assert.Single(await ReadContext(connection).PermissionGrantChangeRequests.ToListAsync());
        Assert.Equal(PermissionGrantChangeRequestStatus.Pending, request.Status);
        Assert.Equal(PermissionGrantEffect.Allow, request.RequestedEffect);
    }

    [Fact]
    public async Task Approving_by_a_different_admin_applies_the_grant_and_marks_the_request_applied()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (requesterSvc, requesterCtx) = Build(connection, requester);
        await requesterSvc.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, "need access", "T-1", requester);
        await requesterCtx.SaveChangesAsync();
        var requestId = (await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync()).Id;

        var (approverSvc, _) = Build(connection, Guid.NewGuid());
        var result = await approverSvc.ApproveAsync(requestId, new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput { DecisionReason = "ok", ExpectedVersion = 1, IdempotencyKey = "approve-1" });

        Assert.Equal(PermissionGrantChangeRequestStatus.Applied, result.Status);
        var grant = Assert.Single(await ReadContext(connection).PermissionGrants.ToListAsync());
        Assert.Equal(Permission, grant.Name);
        Assert.Equal(PermissionGrantEffect.Allow, grant.Effect);
    }

    [Fact]
    public async Task A_requester_cannot_approve_their_own_request()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (svc, ctx) = Build(connection, requester);
        await svc.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, null, null, requester);
        await ctx.SaveChangesAsync();
        var requestId = (await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync()).Id;

        var (selfApproveSvc, _) = Build(connection, requester);
        await Assert.ThrowsAsync<SharedValidationException>(
            () => selfApproveSvc.ApproveAsync(requestId, new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput { DecisionReason = "self", ExpectedVersion = 1, IdempotencyKey = "self-1" }));

        Assert.Empty(await ReadContext(connection).PermissionGrants.ToListAsync());
    }

    [Fact]
    public async Task Approving_a_resource_scoped_request_fails_closed_without_an_owning_resource_coordinator()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var createdAtUtc = DateTime.UtcNow;
        var resourceKey = Guid.CreateVersion7().ToString("D");
        var definition = new PermissionDelegationDefinition(Guid.CreateVersion7(), Permission, "SampleOwner");
        var definitionVersion = new PermissionDelegationDefinitionVersion(Guid.CreateVersion7(), definition.Id, 1,
            new string('a', 64), "Permissions.TenantAccess.Manage", PermissionDelegationTargetKinds.User,
            PermissionDelegationScopeKinds.Resource, PermissionDelegationEffects.Allow, 3600, true,
            "Permissions.TenantDelegation.Approvals.Decide", null, null, false, createdAtUtc, null);
        definitionVersion.Publish();
        var assignmentOperation = new ResourcePermissionAssignmentOperation(
            Guid.CreateVersion7(),
            TenantId,
            requester,
            "sample-resource",
            resourceKey,
            ProviderName,
            ProviderKey,
            "resource-assignment-1",
            new string('b', 64),
            createdAtUtc);
        var request = new PermissionGrantChangeRequest(
            Guid.CreateVersion7(),
            TenantId,
            Permission,
            ProviderName,
            ProviderKey,
            PermissionGrantEffect.Allow,
            expiresAtUtc: null,
            reason: "resource access",
            ticketId: null,
            requestedByUserId: requester,
            createdAtUtc: createdAtUtc);
        request.BindOwnerValidatedResourceScope(
            PermissionDelegationScopeKinds.Resource,
            ownerServiceName: "SampleOwner",
            resourceName: "sample-resource",
            resourceKey: resourceKey,
            ownerRevision: 1,
            receiptId: Guid.CreateVersion7(),
            receiptRevision: 1,
            receiptExpiresAtUtc: createdAtUtc.AddMinutes(5),
            delegationDefinitionVersionId: definitionVersion.Id,
            expectedAssignmentVersion: Guid.CreateVersion7(),
            resourceAssignmentOperationId: assignmentOperation.Id,
            reviewState: ResourceAccessReviewState.NotRequired,
            reviewDueAtUtc: null);
        await using (var seedContext = ReadContext(connection))
        {
            // Definition and version reference each other, so the current-version pointer is set
            // only after both rows exist.
            seedContext.PermissionDelegationDefinitions.Add(definition);
            seedContext.PermissionDelegationDefinitionVersions.Add(definitionVersion);
            seedContext.ResourcePermissionAssignmentOperations.Add(assignmentOperation);
            await seedContext.SaveChangesAsync();
            definition.Publish(definitionVersion.Id);
            seedContext.PermissionGrantChangeRequests.Add(request);
            await seedContext.SaveChangesAsync();
        }

        var (approverSvc, _) = Build(connection, Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<SharedValidationException>(() => approverSvc.ApproveAsync(
            request.Id,
            new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput
            {
                DecisionReason = "ok",
                ExpectedVersion = 1,
                IdempotencyKey = "approve-resource-1"
            }));

        Assert.Equal(
            "Resource-scoped grant approvals require an owning resource coordinator, and none is registered.",
            exception.Message);
        Assert.Empty(await ReadContext(connection).PermissionGrants.ToListAsync());
        Assert.Empty(await ReadContext(connection).ResourcePermissionGrants.ToListAsync());
        Assert.Empty(await ReadContext(connection).PermissionApprovalDecisionOperations.ToListAsync());
        var stored = await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync();
        Assert.Equal(PermissionGrantChangeRequestStatus.Pending, stored.Status);
        Assert.Equal(PermissionGrantScopeKind.Resource, stored.GrantScopeKind);
    }

    [Fact]
    public async Task Rejecting_does_not_apply_a_grant()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (svc, ctx) = Build(connection, requester);
        await svc.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, null, null, requester);
        await ctx.SaveChangesAsync();
        var requestId = (await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync()).Id;

        var (approverSvc, _) = Build(connection, Guid.NewGuid());
        var result = await approverSvc.RejectAsync(requestId, new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput { DecisionReason = "no", ExpectedVersion = 1, IdempotencyKey = "reject-1" });

        Assert.Equal(PermissionGrantChangeRequestStatus.Rejected, result.Status);
        Assert.Empty(await ReadContext(connection).PermissionGrants.ToListAsync());
    }

    [Fact]
    public async Task Approving_with_the_same_idempotency_key_replays_the_stored_result()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (requesterService, requesterContext) = Build(connection, requester);
        await requesterService.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, "need access", null, requester);
        await requesterContext.SaveChangesAsync();
        var requestId = (await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync()).Id;
        var (approver, _) = Build(connection, Guid.NewGuid());
        var input = new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput { DecisionReason = "approved", ExpectedVersion = 1, IdempotencyKey = "replay-approve" };

        var first = await approver.ApproveAsync(requestId, input);
        var replay = await approver.ApproveAsync(requestId, input);

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await ReadContext(connection).PermissionApprovalDecisionOperations.ToListAsync());
    }

    [Fact]
    public async Task Approving_with_a_reused_idempotency_key_and_different_reason_conflicts()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (requesterService, requesterContext) = Build(connection, requester);
        await requesterService.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, "need access", null, requester);
        await requesterContext.SaveChangesAsync();
        var requestId = (await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync()).Id;
        var (approver, _) = Build(connection, Guid.NewGuid());
        await approver.ApproveAsync(requestId, new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput { DecisionReason = "approved", ExpectedVersion = 1, IdempotencyKey = "reused-key" });

        var exception = await Assert.ThrowsAsync<PermissionApprovalConflictException>(() => approver.ApproveAsync(requestId, new Application.Contracts.PermissionApproval.DecideGrantChangeRequestInput { DecisionReason = "different", ExpectedVersion = 1, IdempotencyKey = "reused-key" }));

        Assert.Equal(Application.Contracts.PermissionApproval.PermissionApprovalConflictCode.IdempotencyKeyReused, exception.Conflict.Code);
    }

    [Fact]
    public async Task Administrative_cancellation_with_the_same_idempotency_key_replays_the_stored_result()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (requesterService, requesterContext) = Build(connection, requester);
        await requesterService.CreatePendingAsync(
            TenantId,
            ProviderName,
            ProviderKey,
            Permission,
            null,
            "need access",
            null,
            requester);
        await requesterContext.SaveChangesAsync();
        var requestId = (await ReadContext(connection).PermissionGrantChangeRequests.SingleAsync()).Id;
        var (administrator, _) = Build(connection, Guid.NewGuid());
        var input = new Application.Contracts.PermissionApproval.CancelGrantChangeRequestInput
        {
            Reason = "administratively withdrawn",
            ExpectedVersion = 1,
            IdempotencyKey = "replay-cancel-any"
        };

        var first = await administrator.CancelAnyAsync(requestId, input);
        var replay = await administrator.CancelAnyAsync(requestId, input);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(PermissionGrantChangeRequestStatus.Cancelled, replay.Status);
        Assert.Single(await ReadContext(connection).PermissionApprovalDecisionOperations.ToListAsync());
    }

    [Fact]
    public async Task Re_requesting_supersedes_the_prior_pending_request()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        var (svc, ctx) = Build(connection, requester);

        await svc.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, "first", null, requester);
        await ctx.SaveChangesAsync();
        var (svc2, ctx2) = Build(connection, requester);
        await svc2.CreatePendingAsync(TenantId, ProviderName, ProviderKey, Permission, null, "second", null, requester);
        await ctx2.SaveChangesAsync();

        var all = await ReadContext(connection).PermissionGrantChangeRequests.ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, x => x.Status == PermissionGrantChangeRequestStatus.Pending);
        Assert.Single(all, x => x.Status == PermissionGrantChangeRequestStatus.Cancelled);
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, TenantId);
        context.Database.EnsureCreated();
        return connection;
    }

    private static PermissionsDbContextApp ReadContext(SqliteConnection connection)
        => PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, TenantId, multiTenantFilterEnabled: true);

    private static (PermissionApprovalAppService Service, PermissionsDbContextApp Context) Build(SqliteConnection connection, Guid? currentUserId)
    {
        var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, TenantId, multiTenantFilterEnabled: true);
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var requestRepository = new EfTestRepository<PermissionGrantChangeRequest, Guid>(context);
        var historyRepository = new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var currentUser = new TestCurrentUser(currentUserId);

        var service = new PermissionApprovalAppService(
            requestRepository,
            grantRepository,
            new EfTestRepository<PermissionApprovalDecisionOperation, Guid>(context),
            new TestCurrentTenant(TenantId),
            currentUser,
            stampService,
            grantCacheSync,
            new Permissions.Application.Grants.PermissionGrantHistoryRecorder(historyRepository, currentUser),
            new AllowPendingPermissionGrantValidator(),
            new PermissionDefinitionManager([]),
            new NoopPermissionApprovalDisplayProjectionResolver(),
            new NoopPermissionApprovalNotificationPublisher());

        return (service, context);
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public TestCurrentUser(Guid? id) => Id = id;

        public bool IsAuthenticated => Id.HasValue;
        public Guid? Id { get; }
        public Guid? TenantId => PermissionApprovalWorkflowTests.TenantId;
        public string? SessionId => null;
        public string? UserName => null;
        public string? Name => null;
        public string? SurName => null;
        public string? Email => null;
        public bool EmailVerified => false;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string[] Roles => [];
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => [];
        public System.Security.Claims.Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }
}
