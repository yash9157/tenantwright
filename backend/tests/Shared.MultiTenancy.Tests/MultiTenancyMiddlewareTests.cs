using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.AspNetCore.Host.Middleware;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class MultiTenancyMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_Returns_400_When_Tenant_Input_Is_Invalid()
    {
        var httpContext = new DefaultHttpContext();
        var middleware = CreateMiddleware(
            currentTenant: new TestCurrentTenant(),
            tenantResolver: new TestTenantResolver(new TenantResolveResult
            {
                Status = TenantResolveStatus.InvalidInput,
                Source = HeaderTenantResolveContributor.ContributorName
            }),
            tenantStore: new TestTenantStore(_ => null));

        var nextCalled = false;
        await middleware.InvokeAsync(httpContext, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Returns_403_When_Resolver_Reports_Mismatch()
    {
        var tenantId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var httpContext = new DefaultHttpContext();
        var middleware = CreateMiddleware(
            currentTenant: new TestCurrentTenant(),
            tenantResolver: new TestTenantResolver(new TenantResolveResult
            {
                Status = TenantResolveStatus.TenantMismatch,
                Source = CurrentUserTenantResolveContributor.ContributorName,
                TenantId = tenantId
            }),
            tenantStore: new TestTenantStore(_ => null));

        var nextCalled = false;
        await middleware.InvokeAsync(httpContext, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Returns_403_When_Tenant_Not_Found()
    {
        var tenantId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var httpContext = new DefaultHttpContext();
        var middleware = CreateMiddleware(
            currentTenant: new TestCurrentTenant(),
            tenantResolver: new TestTenantResolver(new TenantResolveResult
            {
                Status = TenantResolveStatus.Resolved,
                TenantId = tenantId,
                Source = HeaderTenantResolveContributor.ContributorName
            }),
            tenantStore: new TestTenantStore(_ => null));

        var nextCalled = false;
        await middleware.InvokeAsync(httpContext, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Returns_403_When_Tenant_Inactive()
    {
        var tenantId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var httpContext = new DefaultHttpContext();
        var middleware = CreateMiddleware(
            currentTenant: new TestCurrentTenant(),
            tenantResolver: new TestTenantResolver(new TenantResolveResult
            {
                Status = TenantResolveStatus.Resolved,
                TenantId = tenantId,
                Source = HeaderTenantResolveContributor.ContributorName
            }),
            tenantStore: new TestTenantStore(_ => new TenantConfiguration
            {
                Id = tenantId,
                Name = "acme",
                NormalizedName = "ACME",
                IsActive = false
            }));

        var nextCalled = false;
        await middleware.InvokeAsync(httpContext, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Caches_Resolver_Result_Per_Request()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var httpContext = new DefaultHttpContext();
        var tenantResolver = new TestTenantResolver(new TenantResolveResult
        {
            Status = TenantResolveStatus.Resolved,
            TenantId = tenantId,
            Source = HeaderTenantResolveContributor.ContributorName
        });

        var middleware = CreateMiddleware(
            currentTenant: new TestCurrentTenant(),
            tenantResolver: tenantResolver,
            tenantStore: new TestTenantStore(_ => new TenantConfiguration
            {
                Id = tenantId,
                Name = "acme",
                NormalizedName = "ACME",
                IsActive = true
            }));

        var nextCalled = false;
        await middleware.InvokeAsync(httpContext, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal(1, tenantResolver.ResolveCallCount);
    }

    [Fact]
    public async Task InvokeAsync_Ignores_QueryString_Tenant_Input()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?tenantid=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var currentTenant = new TestCurrentTenant();
        var middleware = CreateMiddleware(
            currentTenant: currentTenant,
            tenantResolver: CreateRealTenantResolver(new SharedTenantResolveOptions()),
            tenantStore: new TestTenantStore(id => new TenantConfiguration
            {
                Id = id,
                Name = "acme",
                NormalizedName = "ACME",
                IsActive = true
            }));

        var nextCalled = false;
        var tenantVisibleInsideNext = false;
        await middleware.InvokeAsync(httpContext, _ =>
        {
            nextCalled = true;
            tenantVisibleInsideNext = currentTenant.IsAvailable;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.False(tenantVisibleInsideNext);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    private static MultiTenancyMiddleware CreateMiddleware(
        ICurrentTenant currentTenant,
        ITenantResolver tenantResolver,
        ITenantStore tenantStore)
    {
        var options = new TestOptionsMonitor<SharedTenantResolveOptions>(new SharedTenantResolveOptions());

        return new MultiTenancyMiddleware(
            currentTenant,
            tenantResolver,
            tenantStore,
            new TestTenantResolveResultAccessor(),
            options,
            NullLogger<MultiTenancyMiddleware>.Instance);
    }

    private static ITenantResolver CreateRealTenantResolver(SharedTenantResolveOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<SharedTenantResolveOptions>>(new TestOptionsMonitor<SharedTenantResolveOptions>(options));
        services.AddTransient<CurrentUserTenantResolveContributor>();
        services.AddTransient<HeaderTenantResolveContributor>();
        services.AddTransient<ITenantResolver, TenantResolver>();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ITenantResolver>();
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name { get; private set; }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            var previousName = Name;
            Id = id;
            Name = name;
            return new DisposeAction(() =>
            {
                Id = previousId;
                Name = previousName;
            });
        }

        private sealed class DisposeAction : IDisposable
        {
            private readonly Action _action;

            public DisposeAction(Action action)
            {
                _action = action;
            }

            public void Dispose()
            {
                _action();
            }
        }
    }

    private sealed class TestTenantResolver : ITenantResolver
    {
        private readonly TenantResolveResult _result;

        public int ResolveCallCount { get; private set; }

        public TestTenantResolver(TenantResolveResult result)
        {
            _result = result;
        }

        public Task<TenantResolveResult> ResolveAsync(TenantResolveRequestContext context, CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class TestTenantResolveResultAccessor : ITenantResolveResultAccessor
    {
        public TenantResolveResult? Result { get; set; }
    }

    private sealed class TestTenantStore : ITenantStore
    {
        private readonly Func<Guid, TenantConfiguration?> _findById;

        public TestTenantStore(Func<Guid, TenantConfiguration?> findById)
        {
            _findById = findById;
        }

        public TenantConfiguration? Find(Guid id)
        {
            return _findById(id);
        }

        public TenantConfiguration? Find(string normalizedName)
        {
            return null;
        }

        public IReadOnlyList<TenantConfiguration> GetList()
        {
            return Array.Empty<TenantConfiguration>();
        }

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_findById(id));
        }

        public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TenantConfiguration?>(null);
        }

        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TenantConfiguration>>(Array.Empty<TenantConfiguration>());
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
