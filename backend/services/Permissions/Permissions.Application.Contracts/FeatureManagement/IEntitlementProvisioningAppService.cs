namespace Permissions.Application.Contracts.FeatureManagement;

public interface IEntitlementProvisioningAppService
{
    Task<TenantEntitlementProvisioningResultDto> ProvisionTenantAsync(
        ProvisionTenantEntitlementsDto input,
        CancellationToken cancellationToken = default);
}
