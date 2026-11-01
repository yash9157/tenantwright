using Auth.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shared.MultiTenancy;
using Xunit;

namespace Auth.Tests;

public sealed class AuthInteractiveRateLimiterTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ShouldLimit_should_not_limit_safe_page_load_methods(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        Assert.False(AuthInteractiveRateLimiter.ShouldLimit(context));
    }

    [Fact]
    public void ShouldLimit_should_limit_form_submissions()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";

        Assert.True(AuthInteractiveRateLimiter.ShouldLimit(context));
    }

    [Fact]
    public void Interactive_partition_uses_resolved_tenant_and_ignores_spoofed_header()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ICurrentTenant>(new FixedCurrentTenant(tenantId))
                .BuildServiceProvider()
        };
        context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString("D");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;

        var key = AuthHostRegistrationExtensions.BuildInteractiveAuthPartitionKey(context, "auth-login");

        Assert.Contains(tenantId.ToString("D"), key, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Request.Headers["X-Tenant-Id"].ToString(), key, StringComparison.Ordinal);
    }

    private sealed class FixedCurrentTenant(Guid? tenantId) : ICurrentTenant
    {
        public bool IsAvailable => tenantId.HasValue;
        public Guid? Id => tenantId;
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => throw new NotSupportedException();
    }
}
