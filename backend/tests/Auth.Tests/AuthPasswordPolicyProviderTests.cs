using Auth.Pages.Account.Validation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Auth.Tests;

public sealed class AuthPasswordPolicyProviderTests
{
    [Fact]
    public void GetPolicy_UsesDevelopmentDefaults()
    {
        var provider = new AuthPasswordPolicyProvider(new FakeHostEnvironment("Development"));

        var policy = provider.GetPolicy();

        Assert.Equal(6, policy.RequiredLength);
        Assert.False(policy.RequireDigit);
        Assert.False(policy.RequireLowercase);
        Assert.False(policy.RequireUppercase);
        Assert.False(policy.RequireNonAlphanumeric);
    }

    [Fact]
    public void GetPolicy_UsesNonDevelopmentDefaults()
    {
        var provider = new AuthPasswordPolicyProvider(new FakeHostEnvironment("Production"));

        var policy = provider.GetPolicy();

        Assert.Equal(8, policy.RequiredLength);
        Assert.True(policy.RequireDigit);
        Assert.True(policy.RequireLowercase);
        Assert.True(policy.RequireUppercase);
        Assert.True(policy.RequireNonAlphanumeric);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "Auth.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
