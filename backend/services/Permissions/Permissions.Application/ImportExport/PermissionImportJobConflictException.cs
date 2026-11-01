using Permissions.Application.Contracts.ImportExport;

namespace Permissions.Application.ImportExport;

/// <summary>Thrown when an import-job apply cannot proceed (invalid/expired/consumed token, or the job
/// is not in a validated state). The controller maps it to HTTP 409 with the carried DTO.</summary>
public sealed class PermissionImportJobConflictException : Exception
{
    public PermissionImportJobConflictDto Conflict { get; }

    public PermissionImportJobConflictException(PermissionImportJobConflictDto conflict)
        : base($"Permission import-job conflict: {conflict.Code}.")
    {
        Conflict = conflict;
    }
}
