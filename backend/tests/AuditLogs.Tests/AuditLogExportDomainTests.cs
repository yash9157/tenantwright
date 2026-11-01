using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogExportDomainTests
{
    [Fact]
    public void MarkCompleted_should_set_completed_payload_metadata()
    {
        var export = CreateExport();

        export.MarkProcessing(DateTime.UtcNow);
        export.MarkCompleted(
            completedAtUtc: DateTime.UtcNow,
            fileName: "audit-logs.csv",
            contentType: "text/csv",
            fileContent: [1, 2, 3],
            compressed: true,
            fileSizeBytes: 3,
            rowCount: 2);

        Assert.Equal(AuditLogExportStatus.Completed, export.Status);
        Assert.Equal("audit-logs.csv", export.FileName);
        Assert.Equal("text/csv", export.ContentType);
        Assert.Equal(3, export.FileSizeBytes);
        Assert.Equal(2, export.RowCount);
        Assert.True(export.FileContentCompressed);
        Assert.NotNull(export.FileContent);
    }

    [Fact]
    public void MarkExpired_should_clear_file_payload()
    {
        var export = CreateExport();
        export.MarkProcessing(DateTime.UtcNow);
        export.MarkCompleted(
            completedAtUtc: DateTime.UtcNow,
            fileName: "audit-logs.csv",
            contentType: "text/csv",
            fileContent: [1, 2, 3],
            compressed: true,
            fileSizeBytes: 3,
            rowCount: 2);

        export.MarkExpired(DateTime.UtcNow);

        Assert.Equal(AuditLogExportStatus.Expired, export.Status);
        Assert.Null(export.FileName);
        Assert.Null(export.ContentType);
        Assert.Null(export.FileContent);
        Assert.Null(export.FileSizeBytes);
        Assert.Null(export.RowCount);
    }

    private static AuditLogExport CreateExport()
    {
        return new AuditLogExport(
            id: 0,
            tenantId: Guid.CreateVersion7(),
            requestedByUserId: Guid.CreateVersion7().ToString("D"),
            format: AuditLogExportFormat.Csv,
            filterSnapshotJson: "{\"maxRowCount\":100}",
            maxRowCount: 100,
            chunkSize: 10,
            requestedAtUtc: DateTime.UtcNow,
            expireAtUtc: DateTime.UtcNow.AddHours(1));
    }
}
