namespace Permissions.Application.Contracts.ImportExport;

/// <summary>
/// Durable permission-import jobs (BE-8): create-by-validate, apply via a one-time token bound to the
/// requester/scope/payload, and rediscoverable history with cancel/retry.
/// </summary>
public interface IPermissionImportJobAppService
{
    Task<PermissionImportJobCreatedDto> CreateAsync(CreatePermissionImportJobDto input, CancellationToken cancellationToken = default);

    Task<PermissionImportJobDto> ApplyAsync(Guid jobId, ApplyPermissionImportJobDto input, CancellationToken cancellationToken = default);

    Task<PagedPermissionImportJobsDto> GetListAsync(PermissionImportJobListInput input, CancellationToken cancellationToken = default);

    Task<PermissionImportJobDto> GetAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<PermissionImportJobDto> CancelAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Re-validates a non-applied job's stored payload and, if valid, issues a fresh apply token.</summary>
    Task<PermissionImportJobCreatedDto> RetryAsync(Guid jobId, CancellationToken cancellationToken = default);
}
