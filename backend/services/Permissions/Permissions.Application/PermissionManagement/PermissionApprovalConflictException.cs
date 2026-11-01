using Permissions.Application.Contracts.PermissionApproval;

namespace Permissions.Application.PermissionManagement;

public sealed class PermissionApprovalConflictException : Exception
{
    public PermissionApprovalConflictDto Conflict { get; }

    public PermissionApprovalConflictException(PermissionApprovalConflictDto conflict)
        : base($"Permission approval conflict: {conflict.Code}.")
    {
        Conflict = conflict;
    }
}
