using Microsoft.Extensions.Logging.Abstractions;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.MultiTenancy;

namespace AuditLogs.Tests.TestDoubles;

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
