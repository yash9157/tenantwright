using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using Xunit;

namespace AuditLogs.Tests;

public class AuditArchiveManifestDomainTests
{
    [Fact]
    public void MarkRestoreQueued_should_require_archived_or_restored_state()
    {
        var manifest = new AuditArchiveManifest(
            id: 1,
            partitionKey: "tenant:host:202401",
            archivedAtUtc: DateTime.UtcNow,
            deleteAfterUtc: DateTime.UtcNow.AddYears(10));

        Assert.Throws<InvalidOperationException>(() => manifest.MarkRestoreQueued());

        manifest.MarkArchived(
            archiveUri: "blob://auditlogs-archive/audit-logs/archive/v1/tenant-host/year=2024/month=01/partition-tenant:host:202401.jsonl.gz",
            rowCount: 10,
            checksum: "ABC123",
            archivedAtUtc: DateTime.UtcNow,
            deleteAfterUtc: DateTime.UtcNow.AddYears(10));
        manifest.MarkRestoreQueued("manual-replay");

        Assert.Equal(AuditArchiveManifestStatus.RestoreQueued, manifest.Status);
    }

    [Fact]
    public void MarkArchiveFailed_should_store_failure_reason_in_extra_properties()
    {
        var manifest = new AuditArchiveManifest(
            id: 2,
            partitionKey: "tenant:host:202402",
            archivedAtUtc: DateTime.UtcNow,
            deleteAfterUtc: DateTime.UtcNow.AddYears(10));

        manifest.MarkArchiveFailed("sample failure");

        Assert.Equal(AuditArchiveManifestStatus.ArchiveFailed, manifest.Status);
        Assert.True(manifest.ExtraProperties.ContainsKey("LastFailureReason"));
    }
}
