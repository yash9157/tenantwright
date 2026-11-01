using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.PermissionApproval;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Tests.TestDoubles;
using Shared.CurrentUser;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Coverage for the paged, scope-filtered approval inbox list (Pending / Mine / History / All)
/// added on top of the existing Flow 6 workflow.
/// </summary>
public class PermissionApprovalInboxTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string ProviderName = "User";
    private const string Permission = "Permissions.Management.Grants";

    [Fact]
    public async Task Cancellation_is_requester_owned_and_versioned()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        await CreatePendingAsync(connection, requester, "owned");
        var id = await FindRequestIdAsync(connection, "owned");
        var (otherService, _) = Build(connection, Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => otherService.CancelAsync(
            id,
            new CancelGrantChangeRequestInput { Reason = "override", ExpectedVersion = 1, IdempotencyKey = "override-1" }));

        var (requesterService, _) = Build(connection, requester);
        await Assert.ThrowsAsync<PermissionApprovalConflictException>(() => requesterService.CancelAsync(
            id,
            new CancelGrantChangeRequestInput { Reason = "stale", ExpectedVersion = 2, IdempotencyKey = "stale-1" }));
    }

    [Fact]
    public async Task Bulk_decision_is_durable_idempotent_and_rejects_key_reuse()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        await CreatePendingAsync(connection, requester, "bulk-a");
        await CreatePendingAsync(connection, requester, "bulk-b");
        var firstId = await FindRequestIdAsync(connection, "bulk-a");
        var secondId = await FindRequestIdAsync(connection, "bulk-b");
        var (service, _) = Build(connection, Guid.NewGuid());
        var input = new PermissionApprovalBulkDecisionInput
        {
            IdempotencyKey = "bulk-key",
            Items =
            [
                new() { Id = firstId, ExpectedVersion = 1, Decision = PermissionApprovalDecision.Reject, DecisionReason = "reviewed" },
                new() { Id = secondId, ExpectedVersion = 1, Decision = PermissionApprovalDecision.Reject, DecisionReason = "reviewed" }
            ]
        };

        var first = await service.DecideBulkAsync(input);
        var replay = await service.DecideBulkAsync(input);

        Assert.Equal(2, first.Items.Count);
        Assert.True(replay.Replayed);
        input.Items[0].DecisionReason = "changed";
        var conflict = await Assert.ThrowsAsync<PermissionApprovalConflictException>(
            () => service.DecideBulkAsync(input));
        Assert.Equal(PermissionApprovalConflictCode.IdempotencyKeyReused, conflict.Conflict.Code);
    }

    [Fact]
    public async Task Pending_scope_pages_and_reports_total_count()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        await CreatePendingAsync(connection, requester, "u1");
        await CreatePendingAsync(connection, requester, "u2");
        await CreatePendingAsync(connection, requester, "u3");

        var (svc, _) = Build(connection, Guid.NewGuid());

        var firstPage = await svc.GetListAsync(new PermissionGrantChangeRequestListInput { MaxResultCount = 2 });
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);

        var secondPage = await svc.GetListAsync(new PermissionGrantChangeRequestListInput { SkipCount = 2, MaxResultCount = 2 });
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Single(secondPage.Items);
        Assert.All(secondPage.Items, x => Assert.Equal(PermissionGrantChangeRequestStatus.Pending, x.Status));
    }

    [Fact]
    public async Task Mine_scope_returns_only_current_users_requests_across_statuses()
    {
        using var connection = CreateConnection();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        await CreatePendingAsync(connection, mine, "u1");
        await CreatePendingAsync(connection, mine, "u2");
        await CreatePendingAsync(connection, other, "u3");
        // Cancel one of "mine" so Mine spans multiple statuses.
        await CancelAsync(connection, mine, "u2");

        var (svc, _) = Build(connection, mine);
        var result = await svc.GetListAsync(new PermissionGrantChangeRequestListInput { Scope = PermissionGrantChangeRequestScope.Mine });

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, x => Assert.Equal(mine, x.RequestedByUserId));
        Assert.Contains(result.Items, x => x.Status == PermissionGrantChangeRequestStatus.Cancelled);
    }

    [Fact]
    public async Task History_scope_returns_decided_only_and_excludes_pending()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        await CreatePendingAsync(connection, requester, "u1");                 // stays pending
        await CreatePendingAsync(connection, requester, "u2");
        await RejectAsync(connection, Guid.NewGuid(), "u2");                   // decided: rejected
        await CreatePendingAsync(connection, requester, "u3");
        await CancelAsync(connection, requester, "u3");                        // decided: cancelled

        var (svc, _) = Build(connection, Guid.NewGuid());

        var history = await svc.GetListAsync(new PermissionGrantChangeRequestListInput { Scope = PermissionGrantChangeRequestScope.History });
        Assert.Equal(2, history.TotalCount);
        Assert.DoesNotContain(history.Items, x => x.Status == PermissionGrantChangeRequestStatus.Pending);

        var pending = await svc.GetListAsync(new PermissionGrantChangeRequestListInput { Scope = PermissionGrantChangeRequestScope.Pending });
        Assert.Equal(1, pending.TotalCount);

        var all = await svc.GetListAsync(new PermissionGrantChangeRequestListInput { Scope = PermissionGrantChangeRequestScope.All });
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task Status_filter_narrows_within_scope()
    {
        using var connection = CreateConnection();
        var requester = Guid.NewGuid();
        await CreatePendingAsync(connection, requester, "u1");
        await CreatePendingAsync(connection, requester, "u2");
        await RejectAsync(connection, Guid.NewGuid(), "u2");

        var (svc, _) = Build(connection, Guid.NewGuid());
        var rejected = await svc.GetListAsync(new PermissionGrantChangeRequestListInput
        {
            Scope = PermissionGrantChangeRequestScope.All,
            Status = PermissionGrantChangeRequestStatus.Rejected
        });

        Assert.Equal(1, rejected.TotalCount);
        Assert.Equal(PermissionGrantChangeRequestStatus.Rejected, Assert.Single(rejected.Items).Status);
    }

    // ---- helpers ----

    private static async Task CreatePendingAsync(SqliteConnection connection, Guid requester, string providerKey)
    {
        var (svc, ctx) = Build(connection, requester);
        await svc.CreatePendingAsync(TenantId, ProviderName, providerKey, Permission, null, "need", null, requester);
        await ctx.SaveChangesAsync();
    }

    private static async Task RejectAsync(SqliteConnection connection, Guid approver, string providerKey)
    {
        var id = await FindRequestIdAsync(connection, providerKey);
        var (svc, _) = Build(connection, approver);
        await svc.RejectAsync(id, new DecideGrantChangeRequestInput { DecisionReason = "no", ExpectedVersion = 1, IdempotencyKey = "reject-1" });
    }

    private static async Task CancelAsync(SqliteConnection connection, Guid actor, string providerKey)
    {
        var id = await FindRequestIdAsync(connection, providerKey);
        var (svc, _) = Build(connection, actor);
        await svc.CancelAsync(id, new CancelGrantChangeRequestInput { Reason = "no longer needed", ExpectedVersion = 1, IdempotencyKey = "cancel-1" });
    }

    private static async Task<Guid> FindRequestIdAsync(SqliteConnection connection, string providerKey)
    {
        var ctx = ReadContext(connection);
        var request = await ctx.PermissionGrantChangeRequests
            .Where(x => x.ProviderKey == providerKey && x.Status == PermissionGrantChangeRequestStatus.Pending)
            .SingleAsync();
        return request.Id;
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
        public Guid? TenantId => PermissionApprovalInboxTests.TenantId;
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
