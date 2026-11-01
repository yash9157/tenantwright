namespace Shared.EntityFrameworkCore.DependencyInjection;

public interface ISharedDbContextConfigurer
{
    void Configure(SharedDbContextConfigurationContext context);
}

public interface ISharedDbContextConfigurer<TDbContext>
    where TDbContext : SharedDbContext<TDbContext>
{
    void Configure(SharedDbContextConfigurationContext<TDbContext> context);
}
