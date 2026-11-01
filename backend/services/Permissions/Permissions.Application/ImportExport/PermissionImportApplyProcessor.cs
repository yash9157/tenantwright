using Microsoft.Extensions.Logging;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.ImportExport;

/// <summary>
/// Runs the apply half of a durable permission-import job off the request thread. Authorization and
/// one-time-token verification already happened when the apply was accepted (the job is
/// <see cref="PermissionImportJobStatus.Applying"/> and its token was consumed), so this stage only
/// re-validates the retained snapshot against the current target and applies it.
/// </summary>
internal sealed class PermissionImportApplyProcessor : ITransientDependency
{
    private readonly IPermissionImportApplyTransaction _transaction;
    private readonly ILogger<PermissionImportApplyProcessor> _logger;

    public PermissionImportApplyProcessor(
        IPermissionImportApplyTransaction transaction,
        ILogger<PermissionImportApplyProcessor> logger)
    {
        _transaction = transaction;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _transaction.ApplyAsync(jobId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Permission import job {JobId} failed while applying.", jobId);
            await _transaction.RecordFailureAsync(
                jobId,
                "The import failed while applying. Re-validate and try again.",
                cancellationToken);
        }
    }
}
