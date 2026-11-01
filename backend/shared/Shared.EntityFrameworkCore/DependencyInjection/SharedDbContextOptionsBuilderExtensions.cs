using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Shared.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class SharedDbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder AddSharedDbContextOptionsExtension(this DbContextOptionsBuilder optionsBuilder)
    {
        var extension = optionsBuilder.Options.FindExtension<SharedDbContextOptionsExtension>() ?? new SharedDbContextOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TDbContext> AddSharedDbContextOptionsExtension<TDbContext>(this DbContextOptionsBuilder<TDbContext> optionsBuilder)
        where TDbContext : DbContext
    {
        var extension = optionsBuilder.Options.FindExtension<SharedDbContextOptionsExtension>() ?? new SharedDbContextOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}