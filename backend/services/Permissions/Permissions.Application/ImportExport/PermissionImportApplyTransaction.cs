using Microsoft.Extensions.Logging;
using Permissions.Application.Contracts.ImportExport;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Domain.ImportExport;
using Permissions.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Attributes;

namespace Permissions.Application.ImportExport;

[ExposeServices(typeof(IPermissionImportApplyTransaction))]
internal sealed class PermissionImportApplyTransaction : IPermissionImportApplyTransaction, ITransientDependency
{
    private const string ProviderGrantsKind = "ProviderGrants";
    private const string ResourceGrantsKind = "ResourceGrants";

    private readonly IRepository<PermissionImportJob, Guid> _jobRepository;
    private readonly IPermissionImportExportAppService _importExport;
    private readonly ILogger<PermissionImportApplyTransaction> _logger;

    public PermissionImportApplyTransaction(
        IRepository<PermissionImportJob, Guid> jobRepository,
        IPermissionImportExportAppService importExport,
        ILogger<PermissionImportApplyTransaction> logger)
    {
        _jobRepository = jobRepository;
        _importExport = importExport;
        _logger = logger;
    }

    [UnitOfWork(isTransactional: true)]
    public async Task ApplyAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobRepository.FindAsync(x => x.Id == jobId, cancellationToken: cancellationToken);
        if (job is null)
        {
            _logger.LogWarning("Permission import job {JobId} was not found; nothing to apply.", jobId);
            return;
        }

        if (job.Status != PermissionImportJobStatus.Applying)
        {
            // A previous delivery already settled the one-time reviewed payload. Never apply it twice.
            _logger.LogInformation(
                "Permission import job {JobId} is {Status}, not Applying; skipping.", jobId, job.Status);
            return;
        }

        var result = await ApplyForAsync(job, cancellationToken);
        if (result.IsValid != true)
        {
            job.MarkFailed(
                "The target changed since validation; the import no longer applies cleanly.",
                DateTime.UtcNow);
        }
        else
        {
            job.MarkApplied(
                result.CreatedCount,
                result.UpdatedCount,
                result.RemovedCount,
                result.UnchangedCount,
                DateTime.UtcNow);
        }

        await _jobRepository.UpdateAsync(job, autoSave: false, cancellationToken: cancellationToken);
    }

    [UnitOfWork(isTransactional: true)]
    public async Task RecordFailureAsync(
        Guid jobId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobRepository.FindAsync(x => x.Id == jobId, cancellationToken: cancellationToken);
        if (job is null || job.Status != PermissionImportJobStatus.Applying)
        {
            return;
        }

        job.MarkFailed(reason, DateTime.UtcNow);
        await _jobRepository.UpdateAsync(job, autoSave: false, cancellationToken: cancellationToken);
    }

    private Task<PermissionImportApplyResultDto> ApplyForAsync(
        PermissionImportJob job,
        CancellationToken cancellationToken)
    {
        var payload = new PermissionCsvImportRequestDto { Content = job.Content };
        return job.Kind switch
        {
            ProviderGrantsKind => _importExport.ApplyProviderGrantCsvImportAsync(
                job.ProviderName!, job.ProviderKey!, payload, cancellationToken),
            ResourceGrantsKind => _importExport.ApplyResourcePermissionCsvImportAsync(
                job.ResourceName!, job.ResourceKey!, job.ProviderName!, job.ProviderKey!, payload, cancellationToken),
            "Catalog" => _importExport.ApplyCatalogCsvImportAsync(payload, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported permission import kind '{job.Kind}'."),
        };
    }
}
