using Shared.BackgroundTasks.Jobs;

namespace Permissions.Application.ImportExport;

/// <summary>
/// Args for the background apply of a durable permission-import job. Only the job id travels; the
/// payload itself stays in the job row, so the queue never carries permission content and the worker
/// always acts on the exact snapshot that was reviewed.
/// </summary>
[BackgroundJobName("permissions.import-apply")]
public sealed record PermissionImportApplyJobArgs(Guid JobId);
