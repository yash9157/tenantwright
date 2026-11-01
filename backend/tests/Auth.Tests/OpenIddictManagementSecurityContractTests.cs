using System.Reflection;
using Auth.Controllers.Management;
using Auth.Models;
using Microsoft.AspNetCore.RateLimiting;
using Shared.AspNetCore.Host.Idempotency;
using Xunit;

namespace Auth.Tests;

public sealed class OpenIddictManagementSecurityContractTests
{
    [Fact]
    public void Update_contract_does_not_expose_a_client_secret()
    {
        Assert.DoesNotContain(
            typeof(OpenIddictApplicationUpdateInput).GetProperties(),
            property => property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_contract_does_not_accept_a_client_secret()
    {
        Assert.DoesNotContain(
            typeof(OpenIddictApplicationCreateInput).GetProperties(),
            property => property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(nameof(OpenIddictManagementController.CreateApplicationAsync))]
    [InlineData(nameof(OpenIddictManagementController.RotateApplicationSecretAsync))]
    public void Reveal_once_responses_are_not_stored_by_the_idempotency_filter(string methodName)
    {
        var method = GetMethod(methodName);
        Assert.Empty(method.GetCustomAttributes<RequireIdempotencyAttribute>());
    }

    [Theory]
    [InlineData(nameof(OpenIddictManagementController.UpdateApplicationAsync))]
    [InlineData(nameof(OpenIddictManagementController.RevokeApplicationSecretAsync))]
    [InlineData(nameof(OpenIddictManagementController.DeleteApplicationAsync))]
    [InlineData(nameof(OpenIddictManagementController.CreateScopeAsync))]
    [InlineData(nameof(OpenIddictManagementController.UpdateScopeAsync))]
    [InlineData(nameof(OpenIddictManagementController.DeleteScopeAsync))]
    public void Secret_free_mutations_retain_idempotency_protection(string methodName)
    {
        Assert.Single(GetMethod(methodName).GetCustomAttributes<RequireIdempotencyAttribute>());
    }

    [Theory]
    [InlineData(nameof(OpenIddictManagementController.CreateApplicationAsync))]
    [InlineData(nameof(OpenIddictManagementController.UpdateApplicationAsync))]
    [InlineData(nameof(OpenIddictManagementController.RotateApplicationSecretAsync))]
    [InlineData(nameof(OpenIddictManagementController.RevokeApplicationSecretAsync))]
    [InlineData(nameof(OpenIddictManagementController.DeleteApplicationAsync))]
    public void Application_mutations_use_the_dedicated_management_rate_limit(string methodName)
    {
        var attribute = Assert.Single(GetMethod(methodName).GetCustomAttributes<EnableRateLimitingAttribute>());
        Assert.Equal("openiddict-management", attribute.PolicyName);
    }

    private static MethodInfo GetMethod(string methodName)
        => typeof(OpenIddictManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == methodName);
}
