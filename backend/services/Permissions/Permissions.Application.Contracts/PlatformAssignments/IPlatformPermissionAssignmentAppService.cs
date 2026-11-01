namespace Permissions.Application.Contracts.PlatformAssignments;

public interface IPlatformPermissionAssignmentAppService
{
    Task<PlatformPermissionAssignmentDto> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default);
    Task<PlatformPermissionAssignmentDto> GetUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<PermissionAssignmentUpdateResultDto> UpdateRoleAsync(Guid roleId, string idempotencyKey, UpdatePlatformPermissionAssignmentRequest input, CancellationToken cancellationToken = default);
    Task<PermissionAssignmentUpdateResultDto> UpdateUserAsync(Guid userId, string idempotencyKey, UpdatePlatformPermissionAssignmentRequest input, CancellationToken cancellationToken = default);
}
