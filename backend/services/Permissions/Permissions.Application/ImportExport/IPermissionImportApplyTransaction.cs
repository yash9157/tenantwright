namespace Permissions.Application.ImportExport;

/// <summary>
/// Owns the independent transaction boundaries used by durable permission-import execution.
/// </summary>
public interface IPermissionImportApplyTransaction
{
    Task ApplyAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task RecordFailureAsync(Guid jobId, string reason, CancellationToken cancellationToken = default);
}
