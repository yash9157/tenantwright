using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// Runs one unit of cross-tenant work once per database of a DbContext: the shared database in host scope, then
/// each dedicated tenant database in its owning tenant's scope, so every DbContext the work resolves (through the
/// unit-of-work provider or <see cref="TenantDatabaseDbContextActivator"/>) connects to that database.
/// <para>
/// A failure in a dedicated database never starves the others or slows them down: it is logged by database key and
/// error type, counted on <c>shared.tenant_databases.fan_out_failures_total</c>, and the database is retried on the
/// next cycle; throwing would put every healthy database behind the failing one's back-off. A shared-database
/// failure is rethrown after the dedicated databases ran, preserving the caller's existing error handling.
/// </para>
/// </summary>
public sealed class TenantDatabaseFanOut : ISingletonDependency
{
    /// <summary>
    /// The largest <c>skip + take</c> window <see cref="QueryPageAsync{TDbContext,TItem}"/> reads from each database
    /// when a view spans dedicated tenant databases.
    /// </summary>
    public const int MaxCrossDatabasePageWindow = 10_000;

    private readonly ITenantDatabaseTopology _topology;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<TenantDatabaseFanOut> _logger;

    public TenantDatabaseFanOut(
        ITenantDatabaseTopology topology,
        ICurrentTenant currentTenant,
        ILogger<TenantDatabaseFanOut> logger)
    {
        _topology = topology;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    /// <summary>
    /// Runs <paramref name="action"/> for every database of <typeparamref name="TDbContext"/> and returns the results
    /// of the databases that succeeded, in topology order.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> ForEachDatabaseAsync<TDbContext, TResult>(
        string work,
        Func<TenantDatabaseScope, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(work);
        ArgumentNullException.ThrowIfNull(action);

        var topology = await _topology.GetSnapshotAsync(typeof(TDbContext), cancellationToken);
        var results = new List<TResult>(topology.Databases.Count);
        ExceptionDispatchInfo? sharedFailure = null;
        foreach (var database in topology.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var scope = new TenantDatabaseScope(database, topology);
                using (_currentTenant.Change(database.TenantId))
                using (TenantDatabaseScope.Use(scope))
                {
                    results.Add(await action(scope, cancellationToken));
                }
            }
            catch (Exception exception) when (database.IsShared &&
                                              (exception is not OperationCanceledException ||
                                               !cancellationToken.IsCancellationRequested))
            {
                // The shared database keeps its pre-existing failure semantics (the caller's error handling and
                // back-off), but only after every dedicated database had its turn.
                sharedFailure = ExceptionDispatchInfo.Capture(exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                TenantDatabaseMetrics.RecordFanOutFailure(
                    topology.ConnectionStringName, work, exception.GetType().Name);
                // Exception type only: provider messages can echo connection or parameter values.
                _logger.LogWarning(
                    "Cross-tenant work {Work} failed for one {ConnectionName} database ({ErrorType}); continuing with the remaining databases. DatabaseKey={DatabaseKey}, Dedicated={Dedicated}.",
                    work,
                    topology.ConnectionStringName,
                    exception.GetType().Name,
                    database.LogKey,
                    !database.IsShared);
            }
        }

        sharedFailure?.Throw();
        return results;
    }

    /// <summary>
    /// Runs a cross-tenant read once per database of <typeparamref name="TDbContext"/> and returns every database's
    /// result in topology order (shared first). Unlike <see cref="ForEachDatabaseAsync{TDbContext,TResult}"/>, which
    /// keeps background work going past a broken database, a read that claims to cover every tenant must never
    /// silently omit one: any database failure is rethrown, and an incomplete topology (tenant list unavailable)
    /// fails with <see cref="TenantDatabaseTopologyUnavailableException"/>. Tenants whose dedicated assignment is
    /// conflicting or unresolvable are already fail-closed everywhere; they are reported in a warning.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> QueryEveryDatabaseAsync<TDbContext, TResult>(
        string work,
        Func<TenantDatabaseScope, CancellationToken, Task<TResult>> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(work);
        ArgumentNullException.ThrowIfNull(query);

        var topology = await GetCompleteSnapshotAsync<TDbContext>(work, cancellationToken);
        return await QueryEveryDatabaseAsync(topology, query, cancellationToken);
    }

    /// <summary>
    /// One offset page of a cross-tenant list ordered by <paramref name="order"/>, merged across every database of
    /// <typeparamref name="TDbContext"/>. With only the shared database the page is queried directly. Otherwise each
    /// database returns its total and its first <c>skip + take</c> rows in the same order, and the merged page is cut
    /// from their union, which is exact because no row is owned by two databases. The per-database window is bounded
    /// by <see cref="MaxCrossDatabasePageWindow"/>; deeper pages are rejected rather than silently truncated.
    /// </summary>
    /// <param name="queryPage">
    /// Runs in the visited database's scope with (skip, take) and must order by the same total order as
    /// <paramref name="order"/> and restrict cross-tenant rows with <c>WhereOwnedByCurrentDatabase</c>.
    /// </param>
    public async Task<TenantDatabasePage<TItem>> QueryPageAsync<TDbContext, TItem>(
        string work,
        int skip,
        int take,
        IComparer<TItem> order,
        Func<TenantDatabaseScope, int, int, CancellationToken, Task<TenantDatabasePage<TItem>>> queryPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(work);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(queryPage);

        var topology = await GetCompleteSnapshotAsync<TDbContext>(work, cancellationToken);
        if (topology.Databases.Count == 1)
        {
            var single = await QueryEveryDatabaseAsync(
                topology,
                (scope, token) => queryPage(scope, skip, take, token),
                cancellationToken);
            return single[0];
        }

        var window = (long)skip + take;
        if (window > MaxCrossDatabasePageWindow)
        {
            throw new SharedValidationException(
                SharedErrorCodes.CrossDatabasePageTooDeep,
                $"Pages beyond the first {MaxCrossDatabasePageWindow} rows of a view spanning dedicated tenant databases are not available; narrow the filters.");
        }

        var pages = await QueryEveryDatabaseAsync(
            topology,
            (scope, token) => queryPage(scope, 0, (int)window, token),
            cancellationToken);
        var items = pages
            .SelectMany(page => page.Items)
            .Order(order)
            .Skip(skip)
            .Take(take)
            .ToList();
        return new TenantDatabasePage<TItem>(pages.Sum(page => page.TotalCount), items);
    }

    /// <summary>
    /// Every database, in topology order (shared first), whose <paramref name="owns"/> probe matches. Used by host work
    /// that addresses one row by key without knowing its tenant: a globally unique key (a Guid id, a provider message
    /// id) takes the first owner, a key only unique per database (an identity column) must reject more than one. When
    /// dedicated databases exist every database is probed with the guarantees of
    /// <see cref="QueryEveryDatabaseAsync{TDbContext,TResult}"/>, so an unreachable database fails the lookup instead of
    /// silently resolving to the shared one; with only the shared database no probe runs and it is the only candidate.
    /// Enter the result with <see cref="EnterDatabase"/>.
    /// </summary>
    public async Task<IReadOnlyList<TenantDatabase>> FindOwningDatabasesAsync<TDbContext>(
        string work,
        Func<TenantDatabaseScope, CancellationToken, Task<bool>> owns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(work);
        ArgumentNullException.ThrowIfNull(owns);
        EnsureHostScope();

        var topology = await GetCompleteSnapshotAsync<TDbContext>(work, cancellationToken);
        if (topology.Databases.Count == 1)
        {
            // The shared database is the only candidate; the caller's own query reports a missing row.
            return [topology.Shared];
        }

        var probes = await QueryEveryDatabaseAsync(
            topology,
            async (scope, token) => (scope.Database, Owns: await owns(scope, token)),
            cancellationToken);
        return probes.Where(probe => probe.Owns).Select(probe => probe.Database).ToArray();
    }

    /// <summary>
    /// The dedicated database host work on one tenant's rows must enter, or <see langword="null"/> when the tenant's
    /// rows are in the shared database (the caller's host scope). Fails closed when the tenant holds a dedicated
    /// assignment that is conflicting or unresolvable, and when the tenant list is unavailable (the tenant's topology is
    /// then unknown), so host writes can never land in the shared database for a tenant whose data lives elsewhere.
    /// Enter the result with <see cref="EnterDatabase"/>.
    /// </summary>
    public async Task<TenantDatabase?> FindTenantDatabaseAsync<TDbContext>(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var topology = await _topology.GetSnapshotAsync(typeof(TDbContext), cancellationToken);
        if (!topology.IsComplete)
        {
            throw new TenantDatabaseTopologyUnavailableException(topology.ConnectionStringName);
        }

        if (topology.FindDedicated(tenantId) is { } dedicated)
        {
            return dedicated;
        }

        if (topology.TenantIdsWithDedicatedAssignment.Contains(tenantId))
        {
            throw new InvalidOperationException(
                "The tenant's dedicated database is not usable; host work on its data is blocked until it is corrected.");
        }

        return null;
    }

    /// <summary>
    /// Enters <paramref name="database"/>: its owning tenant's scope for a dedicated database, otherwise (shared or
    /// null) the caller's unchanged scope. Deliberately synchronous: the ambient tenant is async-local, so a scope
    /// entered inside an async helper would not flow back to its caller. Call it in the method that runs the work,
    /// as <c>using var scope = fanOut.EnterDatabase(await fanOut.FindTenantDatabaseAsync&lt;T&gt;(tenantId));</c>.
    /// </summary>
    public IDisposable EnterDatabase(TenantDatabase? database) =>
        database is { IsShared: false }
            ? _currentTenant.Change(database.TenantId)
            : NoScopeChange.Instance;

    /// <inheritdoc cref="ForEachDatabaseAsync{TDbContext,TResult}"/>
    public Task ForEachDatabaseAsync<TDbContext>(
        string work,
        Func<TenantDatabaseScope, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ForEachDatabaseAsync<TDbContext, bool>(
            work,
            async (scope, token) =>
            {
                await action(scope, token);
                return true;
            },
            cancellationToken);
    }

    private async Task<TenantDatabaseTopologySnapshot> GetCompleteSnapshotAsync<TDbContext>(
        string work,
        CancellationToken cancellationToken)
    {
        var topology = await _topology.GetSnapshotAsync(typeof(TDbContext), cancellationToken);
        if (!topology.IsComplete)
        {
            throw new TenantDatabaseTopologyUnavailableException(topology.ConnectionStringName);
        }

        var excluded = topology.ConflictingTenantIds.Count + topology.UnresolvableTenantIds.Count;
        if (excluded > 0)
        {
            _logger.LogWarning(
                "Cross-tenant read {Work} over {ConnectionName} excludes {ExcludedTenantCount} tenants whose dedicated database is conflicting or unresolvable; their rows are unreachable until the assignment is corrected.",
                work,
                topology.ConnectionStringName,
                excluded);
        }

        return topology;
    }

    private async Task<IReadOnlyList<TResult>> QueryEveryDatabaseAsync<TResult>(
        TenantDatabaseTopologySnapshot topology,
        Func<TenantDatabaseScope, CancellationToken, Task<TResult>> query,
        CancellationToken cancellationToken)
    {
        var results = new List<TResult>(topology.Databases.Count);
        foreach (var database in topology.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = new TenantDatabaseScope(database, topology);
            using (_currentTenant.Change(database.TenantId))
            using (TenantDatabaseScope.Use(scope))
            {
                results.Add(await query(scope, cancellationToken));
            }
        }

        return results;
    }

    private void EnsureHostScope()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new InvalidOperationException("Locating the database that owns a row is host-scope work.");
        }
    }

    private sealed class NoScopeChange : IDisposable
    {
        public static readonly NoScopeChange Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>One page of a list plus the total number of matching rows.</summary>
public sealed record TenantDatabasePage<TItem>(long TotalCount, IReadOnlyList<TItem> Items);

/// <summary>
/// The cached tenant list is unavailable, so the set of dedicated tenant databases is unknown and a cross-tenant read
/// cannot prove it covers every tenant. Transient: the Tenant service's reconcile worker repopulates the list.
/// </summary>
public sealed class TenantDatabaseTopologyUnavailableException : InvalidOperationException
{
    public TenantDatabaseTopologyUnavailableException(string connectionStringName)
        : base($"The tenant database topology for '{connectionStringName}' is unavailable; the cross-tenant read is blocked until the tenant list is cached again.")
    {
    }
}
