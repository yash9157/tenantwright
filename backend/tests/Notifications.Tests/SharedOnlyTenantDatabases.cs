using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Delivery;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.MultiTenancy;

namespace Notifications.Tests;

/// <summary>
/// Database fan-out over a deployment with no dedicated tenant databases: the shared database only.
/// </summary>
internal static class SharedOnlyTenantDatabases
{
    public static TenantDatabaseFanOut FanOut(ICurrentTenant? currentTenant = null) =>
        new(
            new SharedOnlyTopology(),
            currentTenant ?? new TestCurrentTenant(null),
            NullLogger<TenantDatabaseFanOut>.Instance);

    /// <summary>
    /// A locator over the shared database only. It never probes (the shared database is the only candidate), so the
    /// database provider is never used.
    /// </summary>
    public static NotificationDatabaseLocator Locator(ICurrentTenant? currentTenant = null) =>
        new(FanOut(currentTenant), new UnusedDbContextProvider(), new ImmediateUnitOfWorkExecutor());

    private sealed class UnusedDbContextProvider : IDbContextProvider<NotificationsDbContextApp>
    {
        public Task<NotificationsDbContextApp> GetDbContextAsync() =>
            throw new InvalidOperationException("A shared-only topology never probes a database.");
    }

    private sealed class SharedOnlyTopology : ITenantDatabaseTopology
    {
        public Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
            string connectionStringName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TenantDatabaseTopologySnapshot(
                connectionStringName,
                [new TenantDatabase(connectionStringName, null, "shared")],
                new HashSet<Guid>(),
                new HashSet<Guid>(),
                new HashSet<Guid>(),
                IsComplete: true));

        public Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
            Type dbContextType,
            CancellationToken cancellationToken = default) =>
            GetSnapshotAsync(dbContextType.Name, cancellationToken);

        public void Invalidate()
        {
        }
    }
}
