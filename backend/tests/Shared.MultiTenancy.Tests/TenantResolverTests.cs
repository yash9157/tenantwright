using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.MultiTenancy;
using Shared.Security;
using System.Security.Claims;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class TenantResolverTests
{
    [Fact]
    public async Task ResolveAsync_Uses_CurrentUser_Precedence()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var resolver = CreateResolver(new SharedTenantResolveOptions());

        var result = await resolver.ResolveAsync(new TenantResolveRequestContext
        {
            IsAuthenticated = true,
            Claims =
            [
                new Claim(SharedClaimTypes.TenantId, tenantId.ToString("D"))
            ],
            Headers = new Dictionary<string, string[]>
            {
                ["X-Tenant-Id"] = [tenantId.ToString("D")]
            }
        });

        Assert.Equal(TenantResolveStatus.Resolved, result.Status);
        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal(CurrentUserTenantResolveContributor.ContributorName, result.Source);
    }

    [Fact]
    public async Task ResolveAsync_Returns_Mismatch_When_User_And_Header_Differ()
    {
        var userTenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var headerTenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var resolver = CreateResolver(new SharedTenantResolveOptions());

        var result = await resolver.ResolveAsync(new TenantResolveRequestContext
        {
            IsAuthenticated = true,
            Claims =
            [
                new Claim(SharedClaimTypes.TenantId, userTenantId.ToString("D"))
            ],
            Headers = new Dictionary<string, string[]>
            {
                ["X-Tenant-Id"] = [headerTenantId.ToString("D")]
            }
        });

        Assert.Equal(TenantResolveStatus.TenantMismatch, result.Status);
        Assert.Equal(userTenantId, result.TenantId);
    }

    [Fact]
    public async Task ResolveAsync_Returns_Invalid_When_Header_Is_Not_Guid()
    {
        var resolver = CreateResolver(new SharedTenantResolveOptions());

        var result = await resolver.ResolveAsync(new TenantResolveRequestContext
        {
            Headers = new Dictionary<string, string[]>
            {
                ["X-Tenant-Id"] = ["not-a-guid"]
            }
        });

        Assert.Equal(TenantResolveStatus.InvalidInput, result.Status);
        Assert.Equal(HeaderTenantResolveContributor.ContributorName, result.Source);
    }

    [Fact]
    public async Task ResolveAsync_Returns_Ambiguous_When_Header_Has_Multiple_Values()
    {
        var resolver = CreateResolver(new SharedTenantResolveOptions());

        var result = await resolver.ResolveAsync(new TenantResolveRequestContext
        {
            Headers = new Dictionary<string, string[]>
            {
                ["X-Tenant-Id"] =
                [
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
                ]
            }
        });

        Assert.Equal(TenantResolveStatus.AmbiguousInput, result.Status);
        Assert.Equal(HeaderTenantResolveContributor.ContributorName, result.Source);
    }

    [Fact]
    public async Task ResolveAsync_Returns_Unresolved_When_No_Tenant_Data_Provided()
    {
        var resolver = CreateResolver(new SharedTenantResolveOptions());

        var result = await resolver.ResolveAsync(new TenantResolveRequestContext());

        Assert.Equal(TenantResolveStatus.Unresolved, result.Status);
        Assert.Equal("none", result.Source);
        Assert.Null(result.TenantId);
    }

    [Theory]
    [InlineData("tenantid")]
    [InlineData("TenantId")]
    [InlineData("Abp.TenantId")]
    public async Task ResolveAsync_Returns_Unresolved_For_Legacy_Header_Aliases(string headerKey)
    {
        var resolver = CreateResolver(new SharedTenantResolveOptions());

        var result = await resolver.ResolveAsync(new TenantResolveRequestContext
        {
            Headers = new Dictionary<string, string[]>
            {
                [headerKey] = ["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"]
            }
        });

        Assert.Equal(TenantResolveStatus.Unresolved, result.Status);
        Assert.Equal("none", result.Source);
        Assert.Null(result.TenantId);
    }

    private static ITenantResolver CreateResolver(SharedTenantResolveOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<SharedTenantResolveOptions>>(new TestOptionsMonitor<SharedTenantResolveOptions>(options));
        services.AddTransient<CurrentUserTenantResolveContributor>();
        services.AddTransient<HeaderTenantResolveContributor>();
        services.AddTransient<ITenantResolver, TenantResolver>();

        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<ITenantResolver>();
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
