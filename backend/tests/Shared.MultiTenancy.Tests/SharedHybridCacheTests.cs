using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Threading;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public sealed class SharedHybridCacheTests
{
    [Fact]
    public async Task Uow_aware_get_or_create_reads_existing_hybrid_cache_value()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddHybridCache().AddSerializerFactory<SharedHybridCacheSerializerFactory>();
        services.AddOptions();
        services.Configure<SharedHybridCacheOptions>(options =>
        {
            options.HideErrors = false;
            options.GlobalHybridCacheEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5)
            };
        });
        services.Configure<SharedDistributedCacheOptions>(options => options.KeyPrefix = string.Empty);
        services.Configure<System.Text.Json.JsonSerializerOptions>(options =>
        {
            options.PropertyNameCaseInsensitive = true;
            options.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        services.AddSingleton<ICacheTenantResolver, NullCacheTenantResolver>();
        services.AddSingleton<IDistributedCacheKeyNormalizer, DefaultDistributedCacheKeyNormalizer>();
        services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
        services.AddSingleton<IUnitOfWorkManager, TestUnitOfWorkManager>();

        await using var provider = services.BuildServiceProvider();
        var distributedCache = provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();

        var cache = new SharedHybridCache<HybridCacheRegressionItem, string>(
            provider,
            provider.GetRequiredService<IOptions<SharedHybridCacheOptions>>(),
            provider.GetRequiredService<HybridCache>(),
            distributedCache,
            provider.GetRequiredService<ICancellationTokenProvider>(),
            provider.GetRequiredService<IDistributedCacheKeyNormalizer>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IUnitOfWorkManager>());

        await cache.SetAsync("existing", new HybridCacheRegressionItem { Value = "cached" });

        var factoryCalled = false;
        var value = await cache.GetOrCreateAsync(
            "existing",
            () =>
            {
                factoryCalled = true;
                return Task.FromResult(new HybridCacheRegressionItem { Value = "factory" });
            },
            considerUow: true);

        Assert.NotNull(value);
        Assert.Equal("cached", value!.Value);
        Assert.False(factoryCalled);
    }

    [CacheName("HybridCacheRegression")]
    private sealed class HybridCacheRegressionItem
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class TestUnitOfWorkManager : IUnitOfWorkManager, IDisposable
    {
        private readonly TestUnitOfWork _current = new();

        public IUnitOfWork? Current => _current;
        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;

        public void Dispose()
        {
            _current.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        private readonly List<Func<Task>> _completedHandlers = [];

        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed;
        public event EventHandler<UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = new();
        public IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed => false;
        public bool IsCompleted => false;
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider => EmptyServiceProvider.Instance;
        public IReadOnlyDictionary<string, IDatabaseApi> DatabaseApis => new Dictionary<string, IDatabaseApi>();
        public IReadOnlyDictionary<string, ITransactionApi> TransactionApis => new Dictionary<string, ITransactionApi>();

        public void SetOuter(IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.WhenAll(_completedHandlers.Select(x => x()));
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) => _completedHandlers.Add(handler);

        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory)
        {
            if (Items.TryGetValue(key, out var existing))
            {
                return (TItem)existing!;
            }

            var created = factory(key);
            Items[key] = created;
            return created;
        }

        public void AddDatabaseApi(string key, IDatabaseApi api) { }
        public IDatabaseApi? FindDatabaseApi(string key) => null;
        public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
        public void AddTransactionApi(string key, ITransactionApi api) { }
        public ITransactionApi? FindTransactionApi(string key) => null;
        public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
        public void Dispose()
        {
            Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
            GC.SuppressFinalize(this);
        }

        private void SuppressUnusedEventWarning()
        {
            Failed?.Invoke(this, new UnitOfWorkFailedEventArgs(this, null, false));
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
