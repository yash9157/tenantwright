namespace Tenant.Application.Tenants;

public sealed class TenantBootstrapSeedOptions
{
    public bool Enabled { get; set; }

    public TenantBootstrapDefaultTenantOptions DefaultTenant { get; set; } = new();
}

public sealed class TenantBootstrapDefaultTenantOptions
{
    public bool Enabled { get; set; } = true;

    public Guid Id { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public string Name { get; set; } = "default";
}
