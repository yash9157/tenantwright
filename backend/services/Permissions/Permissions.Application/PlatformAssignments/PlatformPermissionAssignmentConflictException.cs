using Permissions.Application.Contracts.PlatformAssignments;

namespace Permissions.Application.PlatformAssignments;

public sealed class PlatformPermissionAssignmentConflictException : Exception
{
    public PermissionAssignmentConflictDto Conflict { get; }

    public PlatformPermissionAssignmentConflictException(PermissionAssignmentConflictDto conflict)
        : base(conflict.Code.ToString())
    {
        Conflict = conflict;
    }
}
