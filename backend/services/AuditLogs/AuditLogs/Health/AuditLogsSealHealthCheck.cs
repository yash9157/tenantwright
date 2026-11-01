using AuditLogs.Application.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Publishing;
using Shared.EntityFrameworkCore;
using Shared.Uow.Abstractions;

namespace AuditLogs.Health;

public sealed class AuditLogsSealHealthCheck : IHealthCheck
{
    private readonly IDbContextProvider<AuditLogsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IOptions<AuditLogSealOptions> _sealOptions;
    private readonly IOptions<AuditLogSignatureOptions> _signatureOptions;
    private readonly IOptions<AuditLogsOperationalHealthOptions> _healthOptions;

    public AuditLogsSealHealthCheck(
        IDbContextProvider<AuditLogsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IOptions<AuditLogSealOptions> sealOptions,
        IOptions<AuditLogSignatureOptions> signatureOptions,
        IOptions<AuditLogsOperationalHealthOptions> healthOptions)
    {
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _sealOptions = sealOptions;
        _signatureOptions = signatureOptions;
        _healthOptions = healthOptions;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var sealOptions = _sealOptions.Value;
        if (!sealOptions.Enabled)
        {
            AuditLogsOperationalHealthTelemetry.RecordSealLag(0);
            return HealthCheckResult.Healthy();
        }

        var signatures = _signatureOptions.Value;
        var keyId = signatures.CurrentKeyId?.Trim();
        if (string.IsNullOrWhiteSpace(keyId) ||
            !signatures.Keys.TryGetValue(keyId, out var key) ||
            string.IsNullOrWhiteSpace(key))
        {
            AuditLogsOperationalHealthTelemetry.RecordSealLag(long.MaxValue);
            return HealthCheckResult.Unhealthy("Audit log sealing key configuration is incomplete.");
        }

        var cutoff = DateTime.UtcNow.AddSeconds(-sealOptions.WatermarkLagSeconds);
        var (newestEligibleId, sealedToId) = await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            async ct =>
            {
                var dbContext = await _dbContextProvider.GetDbContextAsync();
                var newestId = await dbContext.AuditLogs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(auditLog => auditLog.CreationTime <= cutoff)
                    .Select(auditLog => (long?)auditLog.Id)
                    .MaxAsync(ct) ?? 0;
                var newestSealId = await dbContext.AuditLogSeals
                    .AsNoTracking()
                    .Select(seal => (long?)seal.ToId)
                    .MaxAsync(ct) ?? 0;
                return (newestId, newestSealId);
            },
            cancellationToken);
        var lag = Math.Max(0, newestEligibleId - sealedToId);
        AuditLogsOperationalHealthTelemetry.RecordSealLag(lag);
        var maximumLag = Math.Max(0, _healthOptions.Value.MaximumEligibleSealLagRows);

        return lag <= maximumLag
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Audit log seal progress is outside the configured threshold.");
    }
}
