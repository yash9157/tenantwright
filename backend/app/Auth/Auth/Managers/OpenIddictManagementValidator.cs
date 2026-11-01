using Auth.Models;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Managers;

public sealed class OpenIddictManagementValidator
{
    private readonly HashSet<string> _configurationManagedClientIds;
    private readonly HashSet<string> _configurationManagedScopeNames;
    private readonly HashSet<string> _internalClientIds;
    private readonly HashSet<string> _internalScopeNames;
    private readonly bool _isDevelopment;

    public OpenIddictManagementValidator(IConfiguration configuration)
    {
        var internalClientId = configuration["OpenIddict:Clients:Internal:ClientId"] ?? "auth-internal";
        var gatewayClientId = configuration["OpenIddict:Clients:TenantWrightGateway:ClientId"] ?? "tenantwright-gateway";
        var notificationsInternalClientId = configuration["OpenIddict:Clients:NotificationsInternal:ClientId"] ?? "tenantwright-notifications-internal";

        _configurationManagedClientIds = new HashSet<string>(StringComparer.Ordinal)
        {
            internalClientId,
            gatewayClientId,
            notificationsInternalClientId
        };
        _internalClientIds = new HashSet<string>(StringComparer.Ordinal)
        {
            internalClientId,
            notificationsInternalClientId
        };
        _configurationManagedScopeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            Scopes.OpenId,
            Scopes.Profile,
            Scopes.Email,
            Scopes.Roles,
            "identity",
            "tenant",
            "permissions",
            "notifications",
            "auditlogs",
            "workflows",
            "identity-internal",
            "notifications-internal",
            "tenant-internal",
            "permissions-internal",
            "features.check",
            "capabilities.read",
            "entitlements.provision",
            "tenant-serviceability.read"
        };
        _internalScopeNames = _configurationManagedScopeNames
            .Where(name => name.EndsWith("-internal", StringComparison.Ordinal) ||
                name is "features.check" or "capabilities.read" or "entitlements.provision" or "tenant-serviceability.read")
            .ToHashSet(StringComparer.Ordinal);

        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"] ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        _isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> ConfigurationManagedClientIds => _configurationManagedClientIds;
    public IReadOnlySet<string> ConfigurationManagedScopeNames => _configurationManagedScopeNames;

    public bool IsConfigurationManagedClient(string clientId) => _configurationManagedClientIds.Contains(clientId);
    public bool IsConfigurationManagedScope(string name) => _configurationManagedScopeNames.Contains(name);

    public OpenIddictInputValidationResult ValidateApplication(
        string clientId,
        string clientType,
        OpenIddictApplicationPermissionInput? permissions,
        IReadOnlyCollection<string>? redirectUris,
        IReadOnlyCollection<string>? postLogoutRedirectUris,
        bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Invalid("client_id_required", "Client ID is required.");
        }
        if (isCreate && IsConfigurationManagedClient(clientId))
        {
            return Invalid("configuration_managed", "Configuration-managed clients cannot be created through the console.");
        }
        if (!string.Equals(clientType, ClientTypes.Public, StringComparison.Ordinal) &&
            !string.Equals(clientType, ClientTypes.Confidential, StringComparison.Ordinal))
        {
            return Invalid("invalid_client_type", "Client type must be either 'public' or 'confidential'.");
        }
        if (permissions?.GrantTypes == null || permissions.Endpoints == null || permissions.Scopes == null || permissions.ResponseTypes == null ||
            permissions.AdditionalPermissions == null || redirectUris == null || postLogoutRedirectUris == null)
        {
            return Invalid("whole_representation_required", "Permissions, redirect URIs, and post-logout redirect URIs must all be supplied.");
        }

        var values = permissions.GrantTypes
            .Concat(permissions.Endpoints)
            .Concat(permissions.Scopes)
            .Concat(permissions.ResponseTypes)
            .Concat(permissions.AdditionalPermissions);
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            return Invalid("empty_permission", "OAuth capability collections must not contain empty values.");
        }
        if (!_isDevelopment && permissions.GrantTypes.Contains(GrantTypes.Password, StringComparer.Ordinal))
        {
            return Invalid("password_grant", "The password grant is blocked outside Development.");
        }
        if (permissions.GrantTypes.Contains(GrantTypes.TokenExchange, StringComparer.Ordinal))
        {
            return Invalid("token_exchange_managed", "Token exchange is reserved for configuration-managed clients.");
        }
        if (!_internalClientIds.Contains(clientId) && permissions.Scopes.Any(_internalScopeNames.Contains))
        {
            return Invalid("internal_scope", "Internal scopes can only be assigned to the configured internal client.");
        }

        var redirectResult = ValidateUris(redirectUris);
        if (!redirectResult.IsValid)
        {
            return redirectResult;
        }
        return ValidateUris(postLogoutRedirectUris);
    }

    public OpenIddictInputValidationResult ValidateScope(string name, IReadOnlyCollection<string>? resources, bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Invalid("scope_name_required", "Scope name is required.");
        }
        if (isCreate && IsConfigurationManagedScope(name))
        {
            return Invalid("configuration_managed", "Configuration-managed scopes cannot be created through the console.");
        }
        if (resources == null)
        {
            return Invalid("whole_representation_required", "Scope resources must be supplied.");
        }
        if (resources.Any(string.IsNullOrWhiteSpace))
        {
            return Invalid("empty_resource", "Scope resources must not contain empty values.");
        }
        return OpenIddictInputValidationResult.Valid;
    }

    public OpenIddictInputValidationResult ValidateUris(IEnumerable<string> uris)
    {
        foreach (var rawUri in uris)
        {
            if (string.IsNullOrWhiteSpace(rawUri))
            {
                return Invalid("empty_uri", "Redirect URI values must not be empty.");
            }
            if (rawUri.Contains('*', StringComparison.Ordinal))
            {
                return Invalid("wildcard", $"Redirect URIs cannot contain wildcards: '{rawUri}'.");
            }
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var parsed))
            {
                return Invalid("invalid_uri", $"Redirect URI must be absolute: '{rawUri}'.");
            }
            if (!string.IsNullOrEmpty(parsed.Fragment))
            {
                return Invalid("fragment", $"Redirect URIs cannot contain fragments: '{rawUri}'.");
            }
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                return Invalid("userinfo", $"Redirect URIs cannot contain user information: '{rawUri}'.");
            }

            var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var isDevelopmentLocalhost =
                _isDevelopment &&
                string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (!isHttps && !isDevelopmentLocalhost)
            {
                return Invalid("non_https", $"Redirect URIs must use HTTPS outside local Development: '{rawUri}'.");
            }
        }

        return OpenIddictInputValidationResult.Valid;
    }

    private static OpenIddictInputValidationResult Invalid(string reason, string message) => new(false, reason, message);
}

public readonly record struct OpenIddictInputValidationResult(bool IsValid, string? Reason, string? Message)
{
    public static OpenIddictInputValidationResult Valid { get; } = new(true, null, null);
}
