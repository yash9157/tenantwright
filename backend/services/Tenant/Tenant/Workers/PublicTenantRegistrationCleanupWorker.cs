using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Workers;
using Shared.Uow.Abstractions;
using Tenant.Application.Contracts.Tenants;

namespace Tenant.Workers;

public sealed class PublicTenantRegistrationCleanupOptions : BackgroundWorkerOptions
{
    public int ExpiryHours { get; set; } = 72;
    public int BatchSize { get; set; } = 50;
}

/// <summary>Removes only abandoned PublicAuth provisioning records; verified and Super Admin tenants are excluded by query invariants.</summary>
public sealed class PublicTenantRegistrationCleanupWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "tenant-public-registration-cleanup";
    private readonly ITenantAppService _tenantAppService;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IOptionsMonitor<PublicTenantRegistrationCleanupOptions> _options;

    public PublicTenantRegistrationCleanupWorker(
        ITenantAppService tenantAppService,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IOptionsMonitor<PublicTenantRegistrationCleanupOptions> options)
        => (_tenantAppService, _unitOfWorkExecutor, _options) = (tenantAppService, unitOfWorkExecutor, options);

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return BackgroundWorkerExecutionResult.Idle;
        await _unitOfWorkExecutor.ExecuteTransactionalAsync(
            token => _tenantAppService.CleanupExpiredPublicRegistrationsAsync(
                DateTime.UtcNow,
                options.ExpiryHours,
                options.BatchSize,
                token),
            context.CancellationToken);
        return BackgroundWorkerExecutionResult.Idle;
    }
}
