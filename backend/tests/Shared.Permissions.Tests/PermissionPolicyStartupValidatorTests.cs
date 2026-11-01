using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Permissions.Application.Contracts.Definitions;
using Shared.Permissions.Definitions;
using Shared.Permissions.Startup;
using Xunit;

namespace Shared.Permissions.Tests;

public class PermissionPolicyStartupValidatorTests
{
    [Fact]
    public async Task StartAsync_fails_a_non_development_host_for_an_unknown_authorize_policy()
    {
        var validator = CreateValidator(Environments.Production);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("Permission catalog startup validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_fails_development_for_an_unknown_authorize_policy()
    {
        var validator = CreateValidator(Environments.Development);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_fails_when_an_explicit_host_policy_shadows_a_catalog_permission()
    {
        const string policyName = "Permissions.StartupValidator.Unknown";
        var authorizationOptions = new AuthorizationOptions();
        authorizationOptions.AddPolicy(policyName, policy => policy.RequireAuthenticatedUser());
        var definitions = new SharedPermissionDefinitionIndex(
            new SharedPermissionDefinitionManager([new CollidingDefinitionProvider()]));
        var validator = CreateValidator(
            Environments.Development,
            authorizationOptions,
            definitions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));
    }

    private static PermissionPolicyStartupValidator CreateValidator(
        string environmentName,
        AuthorizationOptions? authorizationOptions = null,
        SharedPermissionDefinitionIndex? definitions = null)
    {
        definitions ??= new SharedPermissionDefinitionIndex(
            new SharedPermissionDefinitionManager(Array.Empty<PermissionDefinitionProvider>()));
        var services = new ServiceCollection().BuildServiceProvider();

        return new PermissionPolicyStartupValidator(
            services,
            new TestHostEnvironment(environmentName),
            Options.Create(authorizationOptions ?? new AuthorizationOptions()),
            definitions,
            new PermissionDefinitionProviderLoadDiagnostics(),
            NullLogger<PermissionPolicyStartupValidator>.Instance);
    }

    [Authorize(Policy = "Permissions.StartupValidator.Unknown")]
    private sealed class UnknownPolicyEndpoint;

    private sealed class CollidingDefinitionProvider : PermissionDefinitionProvider
    {
        public override void Define(IPermissionDefinitionContext context)
        {
            context.AddGroup("Permissions", "Permissions")
                .AddPermission("Permissions.StartupValidator.Unknown", "Unknown");
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = typeof(PermissionPolicyStartupValidatorTests).Assembly.GetName().Name!;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
