using Auth.Managers;
using Auth.Models;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using Xunit;

namespace Auth.Tests;

public sealed class OpenIddictManagementValidatorTests
{
    [Fact]
    public void Configuration_managed_scopes_are_exactly_the_seeded_scope_set()
    {
        var validator = CreateValidator();

        Assert.Equal(
            new[]
            {
                OpenIddictConstants.Scopes.Email,
                OpenIddictConstants.Scopes.OpenId,
                OpenIddictConstants.Scopes.Profile,
                OpenIddictConstants.Scopes.Roles,
                "auditlogs",
                "capabilities.read",
                "entitlements.provision",
                "features.check",
                "identity",
                "identity-internal",
                "notifications",
                "notifications-internal",
                "permissions",
                "permissions-internal",
                "tenant",
                "tenant-internal",
                "tenant-serviceability.read",
                "workflows"
            }.Order(StringComparer.Ordinal),
            validator.ConfigurationManagedScopeNames.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("entitlements.provision")]
    [InlineData("tenant-serviceability.read")]
    [InlineData("capabilities.read")]
    [InlineData("features.check")]
    [InlineData("permissions-internal")]
    public void Machine_scopes_are_configuration_managed(string scope)
    {
        var validator = CreateValidator();

        Assert.True(validator.IsConfigurationManagedScope(scope));
        var result = validator.ValidateScope(scope, ["tenant"], isCreate: true);
        Assert.False(result.IsValid);
        Assert.Equal("configuration_managed", result.Reason);
    }

    [Theory]
    [InlineData("https://app.example.test/callback#token", "fragment")]
    [InlineData("https://user:password@app.example.test/callback", "userinfo")]
    [InlineData("https://*.example.test/callback", "wildcard")]
    [InlineData("http://app.example.test/callback", "non_https")]
    public void Redirect_uri_security_rules_reject_unsafe_values(string uri, string expectedReason)
    {
        var result = CreateValidator().ValidateUris([uri]);

        Assert.False(result.IsValid);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void Development_localhost_http_redirect_is_allowed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            })
            .Build();

        var result = new OpenIddictManagementValidator(configuration)
            .ValidateUris(["http://localhost:4200/auth/callback"]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Whole_application_representation_is_required()
    {
        var result = CreateValidator().ValidateApplication(
            "console-client",
            OpenIddictConstants.ClientTypes.Public,
            permissions: null,
            redirectUris: [],
            postLogoutRedirectUris: [],
            isCreate: false);

        Assert.False(result.IsValid);
        Assert.Equal("whole_representation_required", result.Reason);
    }

    [Fact]
    public void Configuration_managed_clients_are_exactly_the_seeded_first_party_clients()
    {
        Assert.Equal(
            new[]
            {
                "auth-internal",
                "tenantwright-gateway",
                "tenantwright-notifications-internal"
            },
            CreateValidator().ConfigurationManagedClientIds.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("tenantwright-gateway")]
    [InlineData("auth-internal")]
    public void Configuration_managed_client_cannot_be_created_by_the_console(string clientId)
    {
        var result = CreateValidator().ValidateApplication(
            clientId,
            OpenIddictConstants.ClientTypes.Public,
            CompletePermissions(),
            [],
            [],
            isCreate: true);

        Assert.False(result.IsValid);
        Assert.Equal("configuration_managed", result.Reason);
    }

    [Theory]
    [InlineData("auth-internal", "identity-internal")]
    [InlineData("tenantwright-notifications-internal", "identity-internal")]
    [InlineData("auth-internal", "tenant-internal")]
    [InlineData("tenantwright-notifications-internal", "permissions-internal")]
    public void Dedicated_service_clients_are_configuration_managed_internal_clients(string clientId, string internalScope)
    {
        var validator = CreateValidator();
        Assert.True(validator.IsConfigurationManagedClient(clientId));

        var created = validator.ValidateApplication(
            clientId,
            OpenIddictConstants.ClientTypes.Confidential,
            InternalScopePermissions(internalScope),
            [],
            [],
            isCreate: true);
        var updated = validator.ValidateApplication(
            clientId,
            OpenIddictConstants.ClientTypes.Confidential,
            InternalScopePermissions(internalScope),
            [],
            [],
            isCreate: false);

        Assert.False(created.IsValid);
        Assert.Equal("configuration_managed", created.Reason);
        Assert.True(updated.IsValid);
    }

    [Theory]
    [InlineData("identity-internal")]
    [InlineData("notifications-internal")]
    [InlineData("tenant-internal")]
    [InlineData("permissions-internal")]
    [InlineData("features.check")]
    [InlineData("capabilities.read")]
    [InlineData("entitlements.provision")]
    [InlineData("tenant-serviceability.read")]
    public void Internal_scope_can_only_be_requested_by_an_internal_client(string scope)
    {
        var validator = CreateValidator();
        var externalResult = validator.ValidateApplication(
            "console-client",
            OpenIddictConstants.ClientTypes.Confidential,
            InternalScopePermissions(scope),
            [],
            [],
            isCreate: false);
        var gatewayResult = validator.ValidateApplication(
            "tenantwright-gateway",
            OpenIddictConstants.ClientTypes.Public,
            InternalScopePermissions(scope),
            [],
            [],
            isCreate: false);
        var internalResult = validator.ValidateApplication(
            "tenantwright-notifications-internal",
            OpenIddictConstants.ClientTypes.Confidential,
            InternalScopePermissions(scope),
            [],
            [],
            isCreate: false);

        Assert.False(externalResult.IsValid);
        Assert.Equal("internal_scope", externalResult.Reason);
        Assert.False(gatewayResult.IsValid);
        Assert.Equal("internal_scope", gatewayResult.Reason);
        Assert.True(internalResult.IsValid);
    }

    [Theory]
    [InlineData("console-client")]
    [InlineData("tenantwright-notifications-internal")]
    public void Console_cannot_grant_token_exchange(string clientId)
    {
        var exchange = CreateValidator().ValidateApplication(
            clientId, OpenIddictConstants.ClientTypes.Confidential,
            new OpenIddictApplicationPermissionInput
            {
                GrantTypes = [OpenIddictConstants.GrantTypes.TokenExchange],
                Endpoints = [OpenIddictConstants.Permissions.Endpoints.Token],
                Scopes = [], ResponseTypes = [], AdditionalPermissions = []
            }, [], [], isCreate: false);

        Assert.False(exchange.IsValid);
        Assert.Equal("token_exchange_managed", exchange.Reason);
    }

    private static OpenIddictManagementValidator CreateValidator()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        return new OpenIddictManagementValidator(configuration);
    }

    private static OpenIddictApplicationPermissionInput CompletePermissions() => new()
    {
        GrantTypes = [],
        Endpoints = [],
        Scopes = [],
        ResponseTypes = [],
        AdditionalPermissions = []
    };

    private static OpenIddictApplicationPermissionInput InternalScopePermissions(string scope) => new()
    {
        GrantTypes = [OpenIddictConstants.GrantTypes.ClientCredentials],
        Endpoints = [OpenIddictConstants.Permissions.Endpoints.Token],
        Scopes = [scope],
        ResponseTypes = [],
        AdditionalPermissions = []
    };
}
