using AuditLogs.Application.Auditing;
using AuditLogs.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using Shared.AuditLogs.Publishing;
using Shared.BackgroundTasks.Hangfire;
using Shared.BlobStoring;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogsOperationalHealthTests
{
    [Fact]
    public async Task Hangfire_check_should_fail_closed_when_background_processing_is_disabled()
    {
        var check = new AuditLogsHangfireHealthCheck(
            Options.Create(new BackgroundTasksHangfireOptions { Enabled = false }),
            Options.Create(new AuditLogsOperationalHealthOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Seal_check_should_fail_closed_when_enabled_key_is_missing()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var dbContext = TestDoubles.AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        var check = new AuditLogsSealHealthCheck(
            new TestDoubles.StaticDbContextProvider<AuditLogs.EntityFrameworkCore.EntityFrameworkCore.AuditLogsDbContextApp>(dbContext),
            new TestDoubles.RecordingUnitOfWorkExecutor(),
            Options.Create(new AuditLogSealOptions { Enabled = true }),
            Options.Create(new AuditLogSignatureOptions()),
            Options.Create(new AuditLogsOperationalHealthOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Blob_check_should_skip_storage_when_archive_is_disabled()
    {
        var container = new Mock<IBlobContainer<AuditLogArchiveBlobContainer>>(MockBehavior.Strict);
        var check = new AuditLogsArchiveBlobHealthCheck(
            container.Object,
            Options.Create(new AuditLogArchiveOptions { Enabled = false }),
            Options.Create(new AuditLogsOperationalHealthOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        container.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Blob_check_should_write_read_verify_and_delete_canary()
    {
        var stored = Array.Empty<byte>();
        var deleted = false;
        var container = new Mock<IBlobContainer<AuditLogArchiveBlobContainer>>();
        container
            .Setup(value => value.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), false, It.IsAny<CancellationToken>()))
            .Returns<string, Stream, bool, CancellationToken>(async (_, stream, _, cancellationToken) =>
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                stored = buffer.ToArray();
            });
        container
            .Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(stored, writable: false));
        container
            .Setup(value => value.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => deleted = true);
        var check = new AuditLogsArchiveBlobHealthCheck(
            container.Object,
            Options.Create(new AuditLogArchiveOptions { Enabled = true }),
            Options.Create(new AuditLogsOperationalHealthOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(deleted);
    }
}
