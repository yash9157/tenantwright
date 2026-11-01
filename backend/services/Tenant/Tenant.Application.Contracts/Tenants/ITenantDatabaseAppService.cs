namespace Tenant.Application.Contracts.Tenants;

/// <summary>
/// Host management of tenants' dedicated service databases. Tenants share each service's database by default; a host
/// administrator can opt a tenant in to its own database per service database name in the reviewed catalog. Every
/// operation is host-only and never returns a connection string. Assignments never move data.
/// </summary>
public interface ITenantDatabaseAppService
{
    /// <summary>Status of every dedicatable service database of the tenant.</summary>
    Task<TenantDatabaseListDto> GetDatabasesAsync(Guid id);

    /// <summary>Opens a connection to the tenant's stored dedicated database for <paramref name="name"/>.</summary>
    Task<TenantDatabaseTestResultDto> TestDatabaseAsync(Guid id, string name);

    /// <summary>
    /// Assigns or re-points the tenant's dedicated database for <paramref name="name"/>. Never moves data; every
    /// assignment requires <see cref="SetTenantDatabaseInput.AcknowledgeNoDataMigration"/>.
    /// </summary>
    Task<SetTenantDatabaseResultDto> SetDatabaseAsync(Guid id, string name, SetTenantDatabaseInput input);

    /// <summary>
    /// Returns the tenant to the shared service database for <paramref name="name"/>. Never moves data; requires
    /// <see cref="RemoveTenantDatabaseInput.AcknowledgeNoDataMigration"/>.
    /// </summary>
    Task<TenantDatabaseStatusDto> RemoveDatabaseAsync(Guid id, string name, RemoveTenantDatabaseInput input);
}
