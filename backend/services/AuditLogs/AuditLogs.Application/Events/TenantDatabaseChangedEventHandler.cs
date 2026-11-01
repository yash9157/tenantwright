using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace AuditLogs.Application.Events;

/// <summary>
/// Migrates and seeds a tenant's dedicated AuditLogs database after the Tenant service changed its assignment.
/// Failures throw so the message is retried and eventually parked; the tenant fails closed meanwhile. A tenant cache
/// that has not converged yet (or another replica holding the migration lock) is a transient failure retried on the
/// provider's longer schedule; a parked event is still converged by the migration reconciler once tenant work finds
/// the database unmigrated. A database holding another service's schema is refused before any DDL.
/// </summary>
[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantDatabaseChangedEto>),
    IncludeSelf = true)]
public sealed class TenantDatabaseChangedEventHandler
    : ITenantWrightMessageHandler<TenantDatabaseChangedEto>,
        ITransientDependency
{
    private readonly TenantDatabaseMigrator _migrator;

    public TenantDatabaseChangedEventHandler(TenantDatabaseMigrator migrator) => _migrator = migrator;

    public Task HandleAsync(TenantDatabaseChangedEto eventData, CancellationToken cancellationToken = default) =>
        _migrator.MigrateTenantAsync(
            eventData.TenantId,
            eventData.DatabaseName,
            eventData.TargetFingerprint,
            cancellationToken);
}
