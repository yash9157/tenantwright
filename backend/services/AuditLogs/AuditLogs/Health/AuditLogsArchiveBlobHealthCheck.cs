using System.Security.Cryptography;
using AuditLogs.Application.Auditing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shared.BlobStoring;

namespace AuditLogs.Health;

public sealed class AuditLogsArchiveBlobHealthCheck : IHealthCheck
{
    private static readonly byte[] CanaryPayload = "auditlogs-health"u8.ToArray();
    private readonly IBlobContainer<AuditLogArchiveBlobContainer> _container;
    private readonly IOptions<AuditLogArchiveOptions> _archiveOptions;
    private readonly IOptions<AuditLogsOperationalHealthOptions> _healthOptions;

    public AuditLogsArchiveBlobHealthCheck(
        IBlobContainer<AuditLogArchiveBlobContainer> container,
        IOptions<AuditLogArchiveOptions> archiveOptions,
        IOptions<AuditLogsOperationalHealthOptions> healthOptions)
    {
        _container = container;
        _archiveOptions = archiveOptions;
        _healthOptions = healthOptions;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_archiveOptions.Value.Enabled)
        {
            AuditLogsOperationalHealthTelemetry.RecordArchiveBlob(true);
            return HealthCheckResult.Healthy();
        }

        var prefix = _healthOptions.Value.BlobCanaryPrefix.Trim().TrimStart('/');
        var blobName = $"{prefix.TrimEnd('/')}/{Guid.NewGuid():N}.bin";
        try
        {
            await using var source = new MemoryStream(CanaryPayload, writable: false);
            await _container.SaveAsync(blobName, source, overrideExisting: false, cancellationToken);
            await using var stored = await _container.GetAsync(blobName, cancellationToken);
            using var buffer = new MemoryStream();
            await stored.CopyToAsync(buffer, cancellationToken);

            var healthy = CryptographicOperations.FixedTimeEquals(CanaryPayload, buffer.ToArray());
            AuditLogsOperationalHealthTelemetry.RecordArchiveBlob(healthy);
            return healthy
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Audit archive storage canary content did not match.");
        }
        catch (Exception exception)
        {
            AuditLogsOperationalHealthTelemetry.RecordArchiveBlob(false);
            return HealthCheckResult.Unhealthy("Audit archive storage canary failed.", exception);
        }
        finally
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _container.DeleteAsync(blobName, cleanupTimeout.Token);
            }
            catch
            {
                // The primary check result already captures availability. Cleanup is best effort.
            }
        }
    }
}
