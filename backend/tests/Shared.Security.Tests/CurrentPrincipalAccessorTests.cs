using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.Extensions;
using Shared.Modularity.Modules;
using Shared.Security;
using Xunit;

namespace Shared.Security.Tests;

public class CurrentPrincipalAccessorTests
{
    [Fact]
    public void Change_should_override_and_restore_current_principal()
    {
        var originalPrincipal = Thread.CurrentPrincipal as ClaimsPrincipal;
        Thread.CurrentPrincipal = new ClaimsPrincipal(new ClaimsIdentity());

        try
        {
            var accessor = new ThreadCurrentPrincipalAccessor();
            var userId = Guid.NewGuid().ToString("D");
            var changedPrincipal = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(SharedClaimTypes.UserId, userId)],
                    "test"));

            using (accessor.Change(changedPrincipal))
            {
                Assert.Equal(userId, accessor.Principal.FindFirst(SharedClaimTypes.UserId)?.Value);
            }

            Assert.Null(accessor.Principal.FindFirst(SharedClaimTypes.UserId));
        }
        finally
        {
            Thread.CurrentPrincipal = originalPrincipal;
        }
    }

    [Fact]
    public void HttpContext_accessor_should_fallback_to_thread_principal_when_http_context_user_is_missing()
    {
        var originalPrincipal = Thread.CurrentPrincipal as ClaimsPrincipal;
        var threadPrincipal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(SharedClaimTypes.UserId, "thread-user")],
                "thread"));
        Thread.CurrentPrincipal = threadPrincipal;

        try
        {
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext()
            };

            var accessor = new HttpContextCurrentPrincipalAccessor(httpContextAccessor);
            Assert.Equal("thread-user", accessor.Principal.FindFirst(SharedClaimTypes.UserId)?.Value);

            httpContextAccessor.HttpContext!.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(SharedClaimTypes.UserId, "http-user")],
                    "http"));

            Assert.Equal("http-user", accessor.Principal.FindFirst(SharedClaimTypes.UserId)?.Value);
        }
        finally
        {
            Thread.CurrentPrincipal = originalPrincipal;
        }
    }

    [Fact]
    public void Application_registration_should_resolve_single_current_principal_accessor()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var application = services.AddApplication<SecurityTestModule>(configuration);

        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<ICurrentPrincipalAccessor>();

        Assert.IsType<HttpContextCurrentPrincipalAccessor>(accessor);
        Assert.Equal(1, application.Services.Count(d => d.ServiceType == typeof(ICurrentPrincipalAccessor) && d.ServiceKey == null));
    }

    [DependsOn(typeof(SharedSecurityModule))]
    private sealed class SecurityTestModule : BaseModule
    {
    }
}
