namespace Tenant.Application.Contracts.Tenants;

/// <summary>
/// Internal service-to-service Tenant reads. The host controller owns the M2M
/// authorization boundary; this contract owns the UoW-backed query boundary.
/// </summary>
public interface IInternalTenantQueryAppService
{
    Task<TenantConfigurationDto?> GetConfigurationByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<TenantConfigurationDto?> GetConfigurationByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken = default);

    Task<TenantServiceabilityBatchResult> GetServiceabilityBatchAsync(
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken = default);
}
