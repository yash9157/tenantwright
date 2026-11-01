using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
using Tenant.Application.Tenants;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using TenantAggregate = Tenant.Domain.Tenants.Tenant;

namespace Tenant.Data;

public class DefaultTenantDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly IOptions<TenantBootstrapSeedOptions> _bootstrapSeedOptions;

    public DefaultTenantDataSeedContributor(
        IDbContextProvider<TenantDbContextApp> dbContextProvider,
        IOptions<TenantBootstrapSeedOptions> bootstrapSeedOptions)
    {
        _dbContextProvider = dbContextProvider;
        _bootstrapSeedOptions = bootstrapSeedOptions;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        var options = _bootstrapSeedOptions.Value;
        if (!options.Enabled || !options.DefaultTenant.Enabled)
        {
            return;
        }

        var tenantId = options.DefaultTenant.Id;
        var tenantName = options.DefaultTenant.Name.Trim();
        var normalizedName = tenantName.ToUpperInvariant();

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var exists = await dbContext.Tenants.AnyAsync(x => x.Id == tenantId || x.NormalizedName == normalizedName);
        if (exists)
        {
            return;
        }

        dbContext.Tenants.Add(new TenantAggregate(tenantId, tenantName));
        await dbContext.SaveChangesAsync();
    }
}
