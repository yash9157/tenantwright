using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant;

/// <summary>Builds the model for offline migration tooling without starting a service host.</summary>
public sealed class TenantDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenantDbContextApp>
{
    public TenantDbContextApp CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<TenantDbContextApp>()
            .UseNpgsql("Host=localhost;Database=tenant_design;Username=design")
            .Options);
}
