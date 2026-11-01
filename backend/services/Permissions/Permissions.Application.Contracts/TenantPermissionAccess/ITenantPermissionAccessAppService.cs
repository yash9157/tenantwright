namespace Permissions.Application.Contracts.TenantPermissionAccess;

public interface ITenantPermissionAccessAppService
{
    Task<TenantPermissionAccessDto> GetCurrentTenantAccessAsync(GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken = default);

    /// <summary>Effective access for the signed-in principal in its current scope (host operators
    /// included; no active tenant required).</summary>
    Task<TenantPermissionAccessDto> GetCurrentPrincipalAccessAsync(GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken = default);
}
