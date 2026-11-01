using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Shared.AspNetCore.Host.Idempotency;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class IdempotencyRecordKeyBuilderTests
{
    [Fact]
    public async Task BuildAsync_is_stable_for_a_key_while_body_fingerprints_remain_distinct()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var first = CreateHttpContext("""{"name":"one"}""");
        var second = CreateHttpContext("""{"name":"two"}""");
        var currentTenant = new TestCurrentTenant(tenantId);

        var firstKey = await IdempotencyRecordKeyBuilder.BuildAsync(first, currentTenant, "same-key");
        var secondKey = await IdempotencyRecordKeyBuilder.BuildAsync(second, currentTenant, "same-key");
        var firstFingerprint = await IdempotencyRecordKeyBuilder.ComputeBodyFingerprintAsync(first.Request);
        var secondFingerprint = await IdempotencyRecordKeyBuilder.ComputeBodyFingerprintAsync(second.Request);

        Assert.Equal(firstKey, secondKey);
        Assert.NotEqual(firstFingerprint, secondFingerprint);
        Assert.Equal(0, first.Request.Body.Position);
        Assert.Equal(0, second.Request.Body.Position);
    }

    [Fact]
    public async Task Filter_rejects_same_key_with_a_changed_payload()
    {
        var store = new InMemoryIdempotencyStore();
        var filter = new IdempotencyActionFilter(new ImmediateDistributedLock(), store,
            new TestCurrentTenant(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new StaticOptionsMonitor<IdempotencyOptions>(new()), NullLogger<IdempotencyActionFilter>.Instance);
        var first = CreateExecutingContext("""{"name":"one"}""");
        var calls = 0;

        await filter.OnResourceExecutionAsync(first, () =>
        {
            calls++;
            return Task.FromResult(new ResourceExecutedContext(
                new ActionContext(first.HttpContext, first.RouteData, first.ActionDescriptor), [])
            {
                Result = new OkObjectResult(new { accepted = true })
            });
        });

        var second = CreateExecutingContext("""{"name":"two"}""");
        await filter.OnResourceExecutionAsync(second, () =>
        {
            calls++;
            return Task.FromResult(new ResourceExecutedContext(
                new ActionContext(second.HttpContext, second.RouteData, second.ActionDescriptor), []));
        });

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Equal(1, calls);
        Assert.Contains("idempotency_key_reused", conflict.Value?.ToString(), StringComparison.Ordinal);
    }

    private static ResourceExecutingContext CreateExecutingContext(string body)
    {
        var httpContext = CreateHttpContext(body);
        httpContext.Request.Headers["Idempotency-Key"] = "same-key";
        var descriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor();
        descriptor.EndpointMetadata = new List<object> { new RequireIdempotencyAttribute() };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor,
            new ModelStateDictionary());
        return new ResourceExecutingContext(actionContext, [], []);
    }

    private static DefaultHttpContext CreateHttpContext(string body)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/test";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new Restore(() => Id = previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _restore;

            public Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                _restore();
            }
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
    }

    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyCacheEntry> entries = new();
        public Task<IdempotencyCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(entries.GetValueOrDefault(key));
        public Task SetInProgressAsync(string key, string requestFingerprint, TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            entries[key] = new IdempotencyCacheEntry
            {
                State = IdempotencyRecordState.InProgress,
                RequestFingerprint = requestFingerprint,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            return Task.CompletedTask;
        }
        public Task SetCompletedAsync(string key, string requestFingerprint, int statusCode,
            string? contentType, string? responseBody, TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            entries[key] = new IdempotencyCacheEntry
            {
                State = IdempotencyRecordState.Completed,
                RequestFingerprint = requestFingerprint,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                StatusCode = statusCode,
                ContentType = contentType,
                ResponseBody = responseBody
            };
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateDistributedLock : IDistributedLock
    {
        public Task<IDistributedLockHandle?> TryAcquireAsync(string name,
            DistributedLockAcquireOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDistributedLockHandle?>(new Handle(name));
        public Task<IDistributedLockHandle> AcquireAsync(string name,
            DistributedLockAcquireOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDistributedLockHandle>(new Handle(name));
        private sealed class Handle(string name) : IDistributedLockHandle
        {
            public string Name { get; } = name;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
