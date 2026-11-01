namespace Shared.MultiTenancy.Caching;

public class TenantCacheItem
{
    public bool Exists { get; set; } = true;

    public Guid Id { get; set; }

    public string Name { get; set; } = default!;

    public string NormalizedName { get; set; } = default!;

    public bool IsActive { get; set; } = true;

    public Dictionary<string, string> ConnectionStrings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Names of the service databases this tenant has a dedicated connection string for. Carried on list entries,
    /// which never carry the protected values, so database-topology enumeration needs no per-tenant lookup.
    /// </summary>
    public List<string> DedicatedDatabaseNames { get; set; } = new();
}

public class TenantByNameCacheItem
{
    public bool Exists { get; set; }

    public Guid TenantId { get; set; }

    public TenantCacheItem? Tenant { get; set; }
}

public class TenantListCacheItem
{
    public long Stamp { get; set; }

    public List<TenantCacheItem> Tenants { get; set; } = new();
}

public class TenantListStampCacheItem
{
    public long Stamp { get; set; }
}
