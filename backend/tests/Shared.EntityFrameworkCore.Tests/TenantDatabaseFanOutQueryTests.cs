using Microsoft.Extensions.Logging.Abstractions;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EntityFrameworkCore.Tests.TestDoubles;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Cross-tenant reads through <see cref="TenantDatabaseFanOut"/>: unlike background fan-out they never silently omit a
/// database, merge pages exactly, and bound how deep an offset page may reach across databases.
/// </summary>
public sealed class TenantDatabaseFanOutQueryTests
{
    private static readonly Guid DedicatedTenant = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    [Fact]
    public async Task Query_every_database_visits_each_database_in_its_own_scope()
    {
        var currentTenant = new TestAmbientTenant();
        var fanOut = FanOut(currentTenant, TwoDatabases());

        var visited = await fanOut.QueryEveryDatabaseAsync<object, (Guid? Tenant, Guid? Owner)>(
            "test",
            (scope, _) => Task.FromResult((currentTenant.Id, TenantDatabaseScope.Current?.Database.TenantId)));

        Assert.Equal([(null, null), (DedicatedTenant, DedicatedTenant)], visited);
        Assert.Null(currentTenant.Id);
        Assert.Null(TenantDatabaseScope.Current);
    }

    [Fact]
    public async Task Query_every_database_fails_instead_of_omitting_a_failed_dedicated_database()
    {
        var fanOut = FanOut(new TestAmbientTenant(), TwoDatabases());

        await Assert.ThrowsAsync<TimeoutException>(() => fanOut.QueryEveryDatabaseAsync<object, int>(
            "test",
            (scope, _) => scope.Database.IsShared ? Task.FromResult(1) : throw new TimeoutException()));
    }

    [Fact]
    public async Task Query_every_database_fails_while_the_tenant_list_is_unavailable()
    {
        var fanOut = FanOut(new TestAmbientTenant(), TwoDatabases() with { IsComplete = false });

        await Assert.ThrowsAsync<TenantDatabaseTopologyUnavailableException>(() =>
            fanOut.QueryEveryDatabaseAsync<object, int>("test", (_, _) => Task.FromResult(1)));
    }

    [Fact]
    public async Task Query_page_merges_each_databases_window_into_one_ordered_page()
    {
        var fanOut = FanOut(new TestAmbientTenant(), TwoDatabases());
        int[] shared = [9, 7, 4, 1];
        int[] dedicated = [8, 6, 5];
        var windows = new List<(bool Shared, int Skip, int Take)>();

        var page = await fanOut.QueryPageAsync<object, int>(
            "test",
            skip: 2,
            take: 3,
            Comparer<int>.Create((left, right) => right.CompareTo(left)),
            (scope, skip, take, _) =>
            {
                windows.Add((scope.Database.IsShared, skip, take));
                var rows = scope.Database.IsShared ? shared : dedicated;
                return Task.FromResult(new TenantDatabasePage<int>(rows.Length, rows.Skip(skip).Take(take).ToArray()));
            });

        Assert.Equal(7, page.TotalCount);
        Assert.Equal([7, 6, 5], page.Items);
        Assert.Equal([(true, 0, 5), (false, 0, 5)], windows);
    }

    [Fact]
    public async Task Query_page_passes_offsets_through_when_only_the_shared_database_exists()
    {
        var fanOut = FanOut(new TestAmbientTenant(), SharedOnly());
        var windows = new List<(int Skip, int Take)>();

        var page = await fanOut.QueryPageAsync<object, int>(
            "test",
            skip: 50_000,
            take: 10,
            Comparer<int>.Default,
            (_, skip, take, _) =>
            {
                windows.Add((skip, take));
                return Task.FromResult(new TenantDatabasePage<int>(1, [1]));
            });

        Assert.Equal([(50_000, 10)], windows);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Query_page_rejects_a_window_deeper_than_the_cross_database_bound()
    {
        var fanOut = FanOut(new TestAmbientTenant(), TwoDatabases());

        var exception = await Assert.ThrowsAsync<SharedValidationException>(() => fanOut.QueryPageAsync<object, int>(
            "test",
            skip: TenantDatabaseFanOut.MaxCrossDatabasePageWindow,
            take: 1,
            Comparer<int>.Default,
            (_, _, _, _) => Task.FromResult(new TenantDatabasePage<int>(0, []))));

        Assert.Equal(SharedErrorCodes.CrossDatabasePageTooDeep, exception.Code);
    }

    [Fact]
    public async Task Owning_database_lookup_finds_every_owner_and_entering_it_changes_the_callers_scope()
    {
        var currentTenant = new TestAmbientTenant();
        var fanOut = FanOut(currentTenant, TwoDatabases());

        var owners = await fanOut.FindOwningDatabasesAsync<object>("test", (scope, _) => Task.FromResult(!scope.Database.IsShared));
        using (fanOut.EnterDatabase(Assert.Single(owners)))
        {
            // The ambient tenant is async-local: entering must happen in the caller's own frame to reach it.
            Assert.Equal(DedicatedTenant, currentTenant.Id);
        }

        Assert.Null(currentTenant.Id);
        Assert.Equal(2, (await fanOut.FindOwningDatabasesAsync<object>("test", (_, _) => Task.FromResult(true))).Count);
    }

    [Fact]
    public async Task Tenant_database_lookup_returns_the_dedicated_database_and_fails_closed_when_unknown()
    {
        var currentTenant = new TestAmbientTenant();
        var fanOut = FanOut(currentTenant, TwoDatabases());

        Assert.Equal(DedicatedTenant, (await fanOut.FindTenantDatabaseAsync<object>(DedicatedTenant))!.TenantId);
        Assert.Null(await fanOut.FindTenantDatabaseAsync<object>(Guid.NewGuid()));

        var conflicting = TwoDatabases() with { ConflictingTenantIds = new HashSet<Guid> { DedicatedTenant }, Databases = [TwoDatabases().Shared] };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FanOut(currentTenant, conflicting).FindTenantDatabaseAsync<object>(DedicatedTenant));
        await Assert.ThrowsAsync<TenantDatabaseTopologyUnavailableException>(() =>
            FanOut(currentTenant, SharedOnly() with { IsComplete = false }).FindTenantDatabaseAsync<object>(Guid.NewGuid()));
    }

    private static TenantDatabaseFanOut FanOut(TestAmbientTenant currentTenant, TenantDatabaseTopologySnapshot snapshot) =>
        new(new FixedTopology(snapshot), currentTenant, NullLogger<TenantDatabaseFanOut>.Instance);

    private static TenantDatabaseTopologySnapshot SharedOnly() =>
        new(
            "Test",
            [new TenantDatabase("Test", null, "shared")],
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            IsComplete: true);

    private static TenantDatabaseTopologySnapshot TwoDatabases() =>
        new(
            "Test",
            [new TenantDatabase("Test", null, "shared"), new TenantDatabase("Test", DedicatedTenant, "dedicated")],
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            new HashSet<Guid> { DedicatedTenant },
            IsComplete: true);

    private sealed class FixedTopology(TenantDatabaseTopologySnapshot snapshot) : ITenantDatabaseTopology
    {
        public Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
            string connectionStringName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
            Type dbContextType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public void Invalidate()
        {
        }
    }
}
