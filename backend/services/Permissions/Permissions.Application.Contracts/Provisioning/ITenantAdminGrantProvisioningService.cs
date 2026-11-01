namespace Permissions.Application.Contracts.Provisioning;

public interface ITenantAdminGrantProvisioningService
{
    Task UpsertTenantAdminGrantsAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task DeleteTenantAdminGrantsAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
