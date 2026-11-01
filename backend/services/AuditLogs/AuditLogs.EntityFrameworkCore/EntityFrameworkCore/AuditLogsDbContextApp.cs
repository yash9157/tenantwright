using Microsoft.EntityFrameworkCore;
using AuditLogs.Domain.Auditing;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;

namespace AuditLogs.EntityFrameworkCore.EntityFrameworkCore;

[ConnectionStringName(TenantWrightDatabaseCatalog.AuditLogs)]
public sealed class AuditLogsDbContextApp : SharedDbContext<AuditLogsDbContextApp>
{

    public DbSet<AuditLog> AuditLogs { get; set; } = default!;
    public DbSet<AuditLogAction> AuditLogActions { get; set; } = default!;
    public DbSet<EntityChange> EntityChanges { get; set; } = default!;
    public DbSet<EntityPropertyChange> EntityPropertyChanges { get; set; } = default!;
    public DbSet<AuditLogExport> AuditLogExports { get; set; } = default!;
    public DbSet<AuditArchiveManifest> AuditArchiveManifests { get; set; } = default!;
    public DbSet<AuditLogSeal> AuditLogSeals { get; set; } = default!;
    public DbSet<AuditLogLegalHold> AuditLogLegalHolds { get; set; } = default!;
    public DbSet<AuditArchiveRestoreJob> AuditArchiveRestoreJobs { get; set; } = default!;
    public DbSet<AuditLogSealVerificationRun> AuditLogSealVerificationRuns { get; set; } = default!;

    public AuditLogsDbContextApp(DbContextOptions<AuditLogsDbContextApp> options)
        : base(options)
    {
    }

    public Task AddAuditLogsAsync(IEnumerable<AuditLog> auditLogs, CancellationToken cancellationToken = default)
    {
        return AuditLogs.AddRangeAsync(auditLogs, cancellationToken);
    }

    public void AddAuditLog(AuditLog auditLog)
    {
        AuditLogs.Add(auditLog);
    }

    public void ClearChangeTracker()
    {
        ChangeTracker.Clear();
    }

    public Task<int> DeleteAuditLogsByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        return AuditLogs
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureAuditLogs();
        modelBuilder.AddTenantWrightMessagingEntities();
    }
}
