using Shared.Modularity.Modules;
using Shared.Modularity.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Caching.Serialization;
using Shared.Threading;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Shared.Caching;

public class CachingModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var provider = context.Configuration["Caching:Provider"];
        var valkeyConfiguration = context.Configuration["Caching:Valkey:Configuration"];

        CachingEnvironmentGuard.ValidateProvider(
            provider,
            valkeyConfiguration,
            context.Configuration["Caching:Valkey:Password"],
            context.Configuration["Caching:Valkey:TrustedCaCertificatePath"]);

        context.Services.AddMemoryCache();

        context.Services.TryAddSingleton<CacheSerializer>();
        context.Services.TryAddSingleton<IDistributedCacheSerializer, DefaultDistributedCacheSerializer>();
        context.Services.TryAddSingleton<IDistributedCacheKeyNormalizer, DefaultDistributedCacheKeyNormalizer>();
        context.Services.TryAddSingleton<ICancellationTokenProvider, DefaultCancellationTokenProvider>();
        context.Services.TryAddSingleton<ICacheTenantResolver, NullCacheTenantResolver>();

        context.Services.AddSingleton(typeof(IDistributedCache<>), typeof(DistributedCache<>));
        context.Services.AddSingleton(typeof(IDistributedCache<,>), typeof(DistributedCache<,>));

        context.Services.AddHybridCache().AddSerializerFactory<SharedHybridCacheSerializerFactory>();
        context.Services.AddSingleton(typeof(IHybridCache<>), typeof(SharedHybridCache<>));
        context.Services.AddSingleton(typeof(IHybridCache<,>), typeof(SharedHybridCache<,>));

        context.Services.Configure<SharedDistributedCacheOptions>(options =>
        {
            options.GlobalCacheEntryOptions.SlidingExpiration = TimeSpan.FromMinutes(20);
            options.KeyPrefix = context.Configuration["Caching:KeyPrefix"] ?? string.Empty;
            if (CachingEnvironmentGuard.IsDevelopment())
            {
                options.HideErrors = false;
            }
        });

        context.Services.Configure<SharedHybridCacheOptions>(options =>
        {
            options.GlobalHybridCacheEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(20)
            };
            options.KeyPrefix = context.Configuration["Caching:KeyPrefix"] ?? string.Empty;
            if (CachingEnvironmentGuard.IsDevelopment())
            {
                options.HideErrors = false;
            }
        });

        context.Services.Configure<JsonSerializerOptions>(options =>
        {
            options.PropertyNameCaseInsensitive = true;
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        for (var i = context.Services.Count - 1; i >= 0; i--)
        {
            if (context.Services[i].ServiceType == typeof(Microsoft.Extensions.Caching.Distributed.IDistributedCache))
            {
                context.Services.RemoveAt(i);
            }
        }

        if (string.Equals(provider, "Valkey", StringComparison.OrdinalIgnoreCase))
        {
            context.Services.AddOptions();

            if (!string.IsNullOrWhiteSpace(valkeyConfiguration))
            {
                context.Services.TryAddSingleton<IConnectionMultiplexer>(_ =>
                    ConnectionMultiplexer.Connect(
                        ValkeyConnectionConfiguration.Create(context.Configuration)));
            }

            context.Services.Configure<RedisCacheOptions>(options =>
            {
                var instanceName =
                    context.Configuration["Caching:Valkey:InstanceName"];

                if (!string.IsNullOrWhiteSpace(instanceName))
                {
                    options.InstanceName = instanceName;
                }
            });

            // Ensure the framework Redis-protocol cache uses the shared Valkey connection.
            context.Services.AddOptions<RedisCacheOptions>()
                .Configure<IServiceProvider>((options, sp) =>
                {
                    options.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
                });

            context.Services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache, Shared.Caching.Implementations.ValkeyCache>();
            context.Services.AddSingleton<Shared.Caching.Abstractions.IDistributedCache>(sp => (Shared.Caching.Abstractions.IDistributedCache)sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
        }
        else
        {
            context.Services.AddDistributedMemoryCache();
            context.Services.AddSingleton<Shared.Caching.Abstractions.IDistributedCache, FrameworkDistributedCacheAdapter>();
        }
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var provider = context.ServiceProvider.GetRequiredService<IConfiguration>()["Caching:Provider"];
        if (!string.Equals(provider, "Valkey", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var connectionMultiplexer = context.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            await connectionMultiplexer.GetDatabase().PingAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Valkey startup validation failed. The application will not start until Valkey is reachable.",
                exception);
        }
    }
}
