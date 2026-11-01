using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.EntityFrameworkCore.GlobalFilters;

namespace Shared.EntityFrameworkCore;

public class SharedDbContextOptionsExtension : IDbContextOptionsExtension
{
    public void ApplyServices(IServiceCollection services)
    {
        var serviceDescriptor = services.FirstOrDefault(x => x.ServiceType == typeof(ICompiledQueryCacheKeyGenerator));
        if (serviceDescriptor != null && serviceDescriptor.ImplementationType != null)
        {
            services.Remove(serviceDescriptor);
            services.AddScoped(serviceDescriptor.ImplementationType);
            services.Add(ServiceDescriptor.Scoped<ICompiledQueryCacheKeyGenerator>(provider =>
                ActivatorUtilities.CreateInstance<SharedCompiledQueryCacheKeyGenerator>(provider,
                    (ICompiledQueryCacheKeyGenerator)provider.GetRequiredService(serviceDescriptor.ImplementationType))));
        }

        services.Replace(ServiceDescriptor.Scoped<IAsyncQueryProvider, SharedEntityQueryProvider>());
        services.AddSingleton<SharedEfCoreCurrentDbContext>();
    }

    public void Validate(IDbContextOptions options)
    {
    }

    public DbContextOptionsExtensionInfo Info => new SharedOptionsExtensionInfo(this);

    private class SharedOptionsExtensionInfo : DbContextOptionsExtensionInfo
    {
        public SharedOptionsExtensionInfo(IDbContextOptionsExtension extension)
            : base(extension)
        {
        }

        public override bool IsDatabaseProvider => false;

        public override int GetServiceProviderHashCode()
        {
            return 0;
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
        {
            return other is SharedOptionsExtensionInfo;
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
        }

        public override string LogFragment => "SharedOptionsExtension";
    }
}
