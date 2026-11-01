using System.Security.Claims;
using Auth.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Permissions.Application.Contracts.Definitions;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Auth;
using Xunit;

namespace Auth.Tests;

public sealed class OpenIddictManagementAuthorizationTests
{
    private static readonly HashSet<string> InternalClients = new(StringComparer.Ordinal) { "auth-internal" };
    private static readonly HashSet<string> InteractiveClients = new(StringComparer.Ordinal) { "tenantwright-gateway" };

    [Fact]
    public void Auth_host_registers_its_openiddict_management_definition_provider()
    {
        var services = new ServiceCollection();

        AuthHostModule.RegisterPermissionDefinitionProviders(services);

        using var serviceProvider = services.BuildServiceProvider();
        var providers = serviceProvider.GetServices<PermissionDefinitionProvider>();
        Assert.Contains(providers, provider => provider is AuthPermissionDefinitionProvider);
    }

    [Fact]
    public void Openiddict_host_policies_are_distinct_and_map_to_exact_catalog_permissions()
    {
        Assert.Equal(15, OpenIddictManagementPolicies.Mappings.Count);
        Assert.Equal(
            OpenIddictManagementPolicies.Mappings.Count,
            OpenIddictManagementPolicies.Mappings.Select(mapping => mapping.PolicyName).Distinct().Count());
        Assert.Equal(
            OpenIddictManagementPolicies.Mappings.Count,
            OpenIddictManagementPolicies.Mappings.Select(mapping => mapping.PermissionName).Distinct().Count());
        Assert.DoesNotContain(
            OpenIddictManagementPolicies.Mappings,
            mapping => string.Equals(mapping.PolicyName, mapping.PermissionName, StringComparison.Ordinal));
        Assert.Contains(
            OpenIddictManagementPolicies.Mappings,
            mapping => mapping == (
                OpenIddictManagementPolicies.Default,
                AuthPermissions.OpenIddictManagement.Default));
    }

    [Fact]
    public void Trusted_internal_client_requires_exact_scope_client_and_subject_shape()
    {
        var valid = Principal(
            subject: "auth-internal",
            clientId: "auth-internal",
            scopes: "identity-internal");
        var userToken = Principal(
            subject: Guid.NewGuid().ToString(),
            clientId: "auth-internal",
            scopes: "identity-internal");

        Assert.True(OpenIddictManagementAuthorization.IsTrustedInternalClient(valid, InternalClients));
        Assert.False(OpenIddictManagementAuthorization.IsTrustedInternalClient(userToken, InternalClients));
    }

    [Fact]
    public void Trusted_interactive_user_requires_approved_client_and_identity_scope_not_role()
    {
        var valid = Principal(
            subject: Guid.NewGuid().ToString(),
            clientId: "tenantwright-gateway",
            scopes: "openid identity",
            roles: ["TenantAdmin"]);
        var unapproved = Principal(
            subject: Guid.NewGuid().ToString(),
            clientId: "untrusted-client",
            scopes: "openid identity",
            roles: ["HostAdmin"]);

        Assert.True(OpenIddictManagementAuthorization.IsTrustedInteractiveUser(valid, InteractiveClients));
        Assert.False(OpenIddictManagementAuthorization.IsTrustedInteractiveUser(unapproved, InteractiveClients));
    }

    [Fact]
    public async Task Handler_allows_trusted_internal_client_without_user_grant()
    {
        var checker = new StubPermissionChecker(granted: false);
        var context = await AuthorizeAsync(
            checker,
            Principal("auth-internal", "auth-internal", "identity-internal"),
            AuthPermissions.OpenIddictManagement.Applications.Delete);

        Assert.True(context.HasSucceeded);
        Assert.Equal(0, checker.CallCount);
    }

    [Fact]
    public async Task Handler_allows_approved_interactive_user_only_with_exact_permission()
    {
        var checker = new StubPermissionChecker(
            granted: true,
            expectedPermission: AuthPermissions.OpenIddictManagement.Applications.Read);
        var context = await AuthorizeAsync(
            checker,
            Principal(Guid.NewGuid().ToString(), "tenantwright-gateway", "openid identity", ["TenantAdmin"]),
            AuthPermissions.OpenIddictManagement.Applications.Read);

        Assert.True(context.HasSucceeded);
        Assert.Equal(1, checker.CallCount);
    }

    [Fact]
    public async Task Handler_rejects_role_only_interactive_user_when_permission_is_denied()
    {
        var checker = new StubPermissionChecker(granted: false);
        var context = await AuthorizeAsync(
            checker,
            Principal(Guid.NewGuid().ToString(), "tenantwright-gateway", "openid identity", ["HostAdmin"]),
            AuthPermissions.OpenIddictManagement.Scopes.Update);

        Assert.False(context.HasSucceeded);
        Assert.Equal(1, checker.CallCount);
    }

    [Fact]
    public async Task Handler_rejects_unapproved_interactive_client_without_evaluating_grants()
    {
        var checker = new StubPermissionChecker(granted: true);
        var context = await AuthorizeAsync(
            checker,
            Principal(Guid.NewGuid().ToString(), "untrusted-client", "openid identity", ["HostAdmin"]),
            AuthPermissions.OpenIddictManagement.Scopes.Read);

        Assert.False(context.HasSucceeded);
        Assert.Equal(0, checker.CallCount);
    }

    [Fact]
    public async Task Handler_fails_closed_when_permission_evaluation_throws()
    {
        var checker = new StubPermissionChecker(new InvalidOperationException("permission service unavailable"));
        var context = await AuthorizeAsync(
            checker,
            Principal(Guid.NewGuid().ToString(), "tenantwright-gateway", "openid identity"),
            AuthPermissions.OpenIddictManagement.Applications.Create);

        Assert.False(context.HasSucceeded);
        Assert.Equal(1, checker.CallCount);
    }

    private static async Task<AuthorizationHandlerContext> AuthorizeAsync(
        IPermissionChecker checker,
        ClaimsPrincipal principal,
        string permissionName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenIddict:Management:AllowedClientIds"] = "auth-internal",
                ["OpenIddict:Management:AllowedInteractiveClientIds"] = "tenantwright-gateway"
            })
            .Build();
        var handler = new OpenIddictManagementPermissionHandler(
            checker,
            configuration,
            NullLogger<OpenIddictManagementPermissionHandler>.Instance);
        var requirement = new OpenIddictManagementPermissionRequirement(permissionName);
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(context);
        return context;
    }

    private static ClaimsPrincipal Principal(
        string subject,
        string clientId,
        string scopes,
        string[]? roles = null)
    {
        var claims = new List<Claim>
        {
            new(OpenIddictConstants.Claims.Subject, subject),
            new(OpenIddictConstants.Claims.ClientId, clientId),
            new(OpenIddictConstants.Claims.Scope, scopes)
        };
        claims.AddRange((roles ?? []).Select(role => new Claim(OpenIddictConstants.Claims.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class StubPermissionChecker : IPermissionChecker
    {
        private readonly bool _granted;
        private readonly Exception? _exception;
        private readonly string? _expectedPermission;

        public StubPermissionChecker(bool granted, string? expectedPermission = null)
        {
            _granted = granted;
            _expectedPermission = expectedPermission;
        }

        public StubPermissionChecker(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<bool> IsGrantedAsync(
            ClaimsPrincipal principal,
            string permissionName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_exception != null)
            {
                throw _exception;
            }
            if (_expectedPermission != null)
            {
                Assert.Equal(_expectedPermission, permissionName);
            }
            return Task.FromResult(_granted);
        }

        public Task<Dictionary<string, bool>> IsGrantedAsync(
            ClaimsPrincipal principal,
            IReadOnlyList<string> permissionNames,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(permissionNames.ToDictionary(name => name, _ => _granted));
        }
    }
}
