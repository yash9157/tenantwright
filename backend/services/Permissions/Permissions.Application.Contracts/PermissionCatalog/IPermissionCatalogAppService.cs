namespace Permissions.Application.Contracts.PermissionCatalog;

public interface IPermissionCatalogAppService
{
    Task<GetPermissionCatalogResultDto> GetAsync(CancellationToken cancellationToken = default);
    Task<PermissionCatalogEntryDto> CreateAsync(CreatePermissionCatalogEntryDto input, CancellationToken cancellationToken = default);
    Task<PermissionCatalogEntryDto> UpdateAsync(Guid id, UpdatePermissionCatalogEntryDto input, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default);
}
