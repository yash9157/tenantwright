using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.AspNetCore.Host.Middleware;
using Shared.Caching.Sessions;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class SessionValidationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_Should_Return_401_When_SessionMarker_Missing()
    {
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", Guid.NewGuid().ToString("N")));

        var store = new TestSessionMarkerStore();
        var middleware = CreateMiddleware(store, enabled: true, strict: true);

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("invalid_session", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Should_Allow_Request_When_SessionMarker_Exists()
    {
        var userId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var store = new TestSessionMarkerStore();
        var absUnix = new DateTimeOffset(DateTime.UtcNow.AddDays(7)).ToUnixTimeSeconds();
        var idleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(30)).ToUnixTimeSeconds();
        await store.SetActiveAsync(tenantId, userId, sessionId, $"{absUnix}:{idleUnix}", TimeSpan.FromMinutes(30));
        var middleware = CreateMiddleware(store, enabled: true, strict: true);

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Should_Skip_Session_Check_For_M2M_Subject()
    {
        var context = CreateAuthenticatedContext(
            new Claim("sub", "auth-internal"),
            new Claim("client_id", "auth-internal"));

        var store = new TestSessionMarkerStore();
        var middleware = CreateMiddleware(store, enabled: true, strict: true);

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal(0, store.IsActiveCallCount);
    }

    [Fact]
    public async Task InvokeAsync_Should_Skip_Session_Check_For_M2M_ClientId_With_Guid_Subject()
    {
        var context = CreateAuthenticatedContext(
            new Claim("sub", Guid.NewGuid().ToString("D")),
            new Claim("client_id", "tenant-service"));

        var store = new TestSessionMarkerStore();
        var middleware = CreateMiddleware(store, enabled: true, strict: true);

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal(0, store.IsActiveCallCount);
    }

    [Fact]
    public async Task InvokeAsync_Should_Return_401_When_Cache_Check_Throws_And_Strict_True()
    {
        var userId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", Guid.NewGuid().ToString("N")));

        var store = new ThrowingSessionMarkerStore();
        var middleware = CreateMiddleware(store, enabled: true, strict: true);

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("invalid_session", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Should_Reject_When_IdleTimeout_Expired()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var store = new TestSessionMarkerStore();
        var absUnix = new DateTimeOffset(DateTime.UtcNow.AddDays(7)).ToUnixTimeSeconds();
        var idleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5)).ToUnixTimeSeconds();
        await store.SetActiveAsync(tenantId, userId, sessionId, $"{absUnix}:{idleUnix}", TimeSpan.FromMinutes(30));

        var middleware = CreateMiddleware(store, enabled: true, strict: true);
        var nextCalled = false;
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Should_Allow_And_Slide_When_Active_And_Within_Slide_Window()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var store = new TestSessionMarkerStore();
        var absUnix = new DateTimeOffset(DateTime.UtcNow.AddDays(7)).ToUnixTimeSeconds();
        var idleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(10)).ToUnixTimeSeconds();
        await store.SetActiveAsync(tenantId, userId, sessionId, $"{absUnix}:{idleUnix}", TimeSpan.FromMinutes(30));

        var middleware = CreateMiddleware(store, enabled: true, strict: true);
        var nextCalled = false;
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);

        var updatedMarker = await store.IsActiveAsync(tenantId, userId, sessionId);
        Assert.NotNull(updatedMarker);
        var parts = updatedMarker.Split(':');
        var newIdleUnix = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var expectedIdleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(30)).ToUnixTimeSeconds();
        Assert.True(Math.Abs(newIdleUnix - expectedIdleUnix) <= 5);
    }

    [Fact]
    public async Task InvokeAsync_Should_Not_Slide_When_Active_And_Outside_Slide_Window()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var store = new TestSessionMarkerStore();
        var absUnix = new DateTimeOffset(DateTime.UtcNow.AddDays(7)).ToUnixTimeSeconds();
        var idleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(25)).ToUnixTimeSeconds();
        await store.SetActiveAsync(tenantId, userId, sessionId, $"{absUnix}:{idleUnix}", TimeSpan.FromMinutes(30));

        var middleware = CreateMiddleware(store, enabled: true, strict: true);

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);

        var currentMarker = await store.IsActiveAsync(tenantId, userId, sessionId);
        Assert.NotNull(currentMarker);
        var parts = currentMarker.Split(':');
        var currentIdleUnix = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(idleUnix, currentIdleUnix);
    }

    [Fact]
    public async Task InvokeAsync_Should_Reject_When_AbsoluteTimeout_Expired()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var store = new TestSessionMarkerStore();
        var absUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5)).ToUnixTimeSeconds();
        var idleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(10)).ToUnixTimeSeconds();
        await store.SetActiveAsync(tenantId, userId, sessionId, $"{absUnix}:{idleUnix}", TimeSpan.FromMinutes(30));

        var middleware = CreateMiddleware(store, enabled: true, strict: true);
        var nextCalled = false;
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);

        var marker = await store.IsActiveAsync(tenantId, userId, sessionId);
        Assert.Null(marker);
    }

    [Fact]
    public async Task InvokeAsync_Should_Migrate_Legacy_Marker()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var store = new TestSessionMarkerStore();
        await store.SetActiveAsync(tenantId, userId, sessionId, "1", TimeSpan.FromMinutes(30));

        var middleware = CreateMiddleware(store, enabled: true, strict: true);
        var nextCalled = false;
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);

        var marker = await store.IsActiveAsync(tenantId, userId, sessionId);
        Assert.NotNull(marker);
        Assert.Contains(":", marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Should_Return_401_When_Slide_Write_Throws_And_Strict_True()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var context = CreateAuthenticatedContext(
            new Claim("sub", userId.ToString("D")),
            new Claim("tenantid", tenantId.ToString("D")),
            new Claim("session_id", sessionId));

        var absUnix = new DateTimeOffset(DateTime.UtcNow.AddDays(7)).ToUnixTimeSeconds();
        var idleUnix = new DateTimeOffset(DateTime.UtcNow.AddMinutes(10)).ToUnixTimeSeconds();
        var store = new ThrowingSetSessionMarkerStore($"{absUnix}:{idleUnix}");

        var middleware = CreateMiddleware(store, enabled: true, strict: true);
        var nextCalled = false;
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static SessionValidationMiddleware CreateMiddleware(
        ISessionMarkerStore store,
        bool enabled,
        bool strict,
        SessionValidationOptions? customOptions = null)
    {
        var options = Options.Create(customOptions ?? new SessionValidationOptions
        {
            Enabled = enabled,
            Strict = strict,
            MarkerTtlHours = 168,
            IdleTimeoutMinutes = 30
        });

        return new SessionValidationMiddleware(
            store,
            options,
            NullLogger<SessionValidationMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateAuthenticatedContext(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private sealed class TestSessionMarkerStore : ISessionMarkerStore
    {
        private readonly Dictionary<string, string> _activeKeys = new(StringComparer.Ordinal);
        public int IsActiveCallCount { get; private set; }

        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            _activeKeys[BuildKey(tenantId, userId, sessionId)] = value;
            return Task.CompletedTask;
        }

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
        {
            IsActiveCallCount++;
            _activeKeys.TryGetValue(BuildKey(tenantId, userId, sessionId), out var value);
            return Task.FromResult<string?>(value);
        }

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
        {
            _activeKeys.Remove(BuildKey(tenantId, userId, sessionId));
            return Task.CompletedTask;
        }

        private static string BuildKey(Guid? tenantId, Guid userId, string sessionId)
            => $"{tenantId?.ToString("D") ?? "host"}::{userId:D}::{SessionMarkerKey.NormalizeSessionId(sessionId)}";
    }

    private sealed class ThrowingSessionMarkerStore : ISessionMarkerStore
    {
        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated Valkey failure.");

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingSetSessionMarkerStore : ISessionMarkerStore
    {
        private readonly string _markerValue;

        public ThrowingSetSessionMarkerStore(string markerValue)
        {
            _markerValue = markerValue;
        }

        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated Valkey write failure.");

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(_markerValue);

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
