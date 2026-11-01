namespace Shared.MultiTenancy;

public class TenantConfiguration
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string NormalizedName { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public Dictionary<string, string> ConnectionStrings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Names of the service databases this tenant has a dedicated connection string for. Populated on tenant-list
    /// results as well as single-tenant lookups; it never carries connection-string values.
    /// </summary>
    public IReadOnlyList<string> DedicatedDatabaseNames { get; set; } = [];
}
