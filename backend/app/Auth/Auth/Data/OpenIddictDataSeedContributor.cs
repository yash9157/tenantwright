using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Auth.Managers;
using Auth.Models;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using Shared.AspNetCore.Host.Configuration;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Data;

public class OpenIddictDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private static readonly string[] IdentityScopeResources = ["IdentityService", "identity"];
    private static readonly string[] TenantScopeResources = ["TenantService", "tenant"];
    private static readonly string[] PermissionsScopeResources = ["PermissionsService", "permissions"];
    private static readonly string[] NotificationsScopeResources = ["NotificationsService", "notifications"];
    private static readonly string[] AuditLogsScopeResources = ["AuditLogsService", "auditlogs"];
    private static readonly string[] WorkflowsScopeResources = ["WorkflowsService", "workflows"];

    private readonly IConfiguration _configuration;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly OpenIddictManagementValidator _validator;

    public OpenIddictDataSeedContributor(
        IConfiguration configuration,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        OpenIddictManagementValidator validator)
    {
        _configuration = configuration;
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
        _validator = validator;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        await SeedTenantWrightGatewayClientAsync();
        await SeedInternalClientsAsync();
        await SeedStandardScopesAsync();
        await SeedIdentityApiScopeAsync();
        await SeedTenantApiScopeAsync();
        await SeedPermissionsApiScopeAsync();
        await SeedNotificationsApiScopeAsync();
        await SeedAuditLogsApiScopeAsync();
        await SeedWorkflowsApiScopeAsync();
        await SeedIdentityInternalScopeAsync();
        await SeedNotificationsInternalScopeAsync();
        await SeedTenantInternalScopeAsync();
        await SeedFeaturesCheckScopeAsync();
        await SeedPermissionsInternalScopeAsync();
        await SeedCapabilityMetadataScopeAsync();
        await SeedEntitlementProvisioningScopeAsync();
        await SeedTenantServiceabilityScopeAsync();
        await SeedNotificationsServiceClientAsync();
    }

    private async Task SeedTenantWrightGatewayClientAsync()
    {
        var clientId = _configuration["OpenIddict:Clients:TenantWrightGateway:ClientId"] ?? "tenantwright-gateway";
        var existing = await _applicationManager.FindByClientIdAsync(clientId);
        var clientDisplayName = _configuration["OpenIddict:Clients:TenantWrightGateway:DisplayName"] ?? "TenantWright Gateway Client";
        var clientSecret = _configuration["OpenIddict:Clients:TenantWrightGateway:Secret"];
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        var redirectUris = ReadConfiguredUris(
            "OpenIddict:Clients:TenantWrightGateway:RedirectUris",
            "OpenIddict:Clients:TenantWrightGateway:RedirectUri",
            "https://oauth.pstmn.io/v1/callback",
            allowFallback: isDevelopment);
        var postLogoutRedirectUris = ReadConfiguredUris(
            "OpenIddict:Clients:TenantWrightGateway:PostLogoutRedirectUris",
            "OpenIddict:Clients:TenantWrightGateway:PostLogoutRedirectUri",
            redirectUris[0],
            allowFallback: true);
        EnsureConfiguredUrisAreValid("OpenIddict:Clients:TenantWrightGateway", redirectUris);
        EnsureConfiguredUrisAreValid("OpenIddict:Clients:TenantWrightGateway", postLogoutRedirectUris);
        var allowPasswordFlow =
            isDevelopment
                ? !string.Equals(_configuration["OpenIddict:AllowPasswordFlow"], "false", StringComparison.OrdinalIgnoreCase)
                : string.Equals(_configuration["OpenIddict:AllowPasswordFlow"], "true", StringComparison.OrdinalIgnoreCase);

        var descriptor = new OpenIddictApplicationDescriptor();
        if (existing != null)
        {
            await _applicationManager.PopulateAsync(descriptor, existing);
        }

        descriptor.ClientId = clientId;
        descriptor.DisplayName = clientDisplayName;
        descriptor.ClientType = string.IsNullOrEmpty(clientSecret) ? ClientTypes.Public : ClientTypes.Confidential;
        descriptor.ConsentType = OpenIddictConstants.ConsentTypes.Implicit;

        descriptor.Permissions.Clear();
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.EndSession);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Revocation);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
        if (allowPasswordFlow)
        {
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.Password);
        }
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + Scopes.OpenId);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Email);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Profile);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Roles);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "identity");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "tenant");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "permissions");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "notifications");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "auditlogs");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "workflows");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "offline_access");

        descriptor.RedirectUris.Clear();
        foreach (var redirectUri in redirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(redirectUri));
        }

        descriptor.PostLogoutRedirectUris.Clear();
        foreach (var postLogoutRedirectUri in postLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(postLogoutRedirectUri));
        }

        if (string.IsNullOrEmpty(clientSecret))
        {
            descriptor.ClientSecret = null;
            descriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        }
        else
        {
            descriptor.ClientSecret = clientSecret;
            descriptor.Requirements.Remove(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        }

        MarkConfigurationManaged(descriptor.Properties);
        if (existing == null)
        {
            await _applicationManager.CreateAsync(descriptor);
        }
        else
        {
            await _applicationManager.UpdateAsync(existing, descriptor);
        }
    }

    private async Task SeedStandardScopesAsync()
    {
        await CreateScopeIfMissingAsync(new OpenIddictScopeDescriptor
        {
            Name = Scopes.OpenId,
            DisplayName = "OpenID Connect"
        });

        await CreateScopeIfMissingAsync(new OpenIddictScopeDescriptor
        {
            Name = Scopes.Profile,
            DisplayName = "User profile"
        });

        await CreateScopeIfMissingAsync(new OpenIddictScopeDescriptor
        {
            Name = Scopes.Email,
            DisplayName = "Email address"
        });

        await CreateScopeIfMissingAsync(new OpenIddictScopeDescriptor
        {
            Name = Scopes.Roles,
            DisplayName = "User roles"
        });
    }

    private async Task SeedInternalClientsAsync()
    {
        var clientId = _configuration["OpenIddict:Clients:Internal:ClientId"] ?? "auth-internal";
        var existing = await _applicationManager.FindByClientIdAsync(clientId);
        var clientDisplayName = _configuration["OpenIddict:Clients:Internal:DisplayName"] ?? "Auth Internal Client";
        var clientSecret = _configuration["OpenIddict:Clients:Internal:Secret"];
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-auth",
                key: "OpenIddict:Clients:Internal:Secret",
                rule: "The confidential internal client requires a secret outside Development.",
                fixHint: "Provide OpenIddict__Clients__Internal__Secret through the deployment secret boundary.");
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        if (existing != null)
        {
            await _applicationManager.PopulateAsync(descriptor, existing);
        }

        descriptor.ClientId = clientId;
        descriptor.DisplayName = clientDisplayName;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = OpenIddictConstants.ConsentTypes.Implicit;

        descriptor.Permissions.Clear();
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "identity-internal");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "notifications-internal");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "tenant-internal");
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "features.check");

        descriptor.ClientSecret = clientSecret;

        MarkConfigurationManaged(descriptor.Properties);

        if (existing == null)
        {
            await _applicationManager.CreateAsync(descriptor);
        }
        else
        {
            await _applicationManager.UpdateAsync(existing, descriptor);
        }
    }

    private async Task SeedIdentityApiScopeAsync()
    {
        await UpsertScopeAsync(
            name: "identity",
            displayName: "Identity API",
            resources: IdentityScopeResources);
    }

    private async Task SeedTenantApiScopeAsync()
    {
        await UpsertScopeAsync(
            name: "tenant",
            displayName: "Tenant API",
            resources: TenantScopeResources);
    }

    private async Task SeedPermissionsApiScopeAsync()
    {
        await UpsertScopeAsync(
            name: "permissions",
            displayName: "Permissions API",
            resources: PermissionsScopeResources);
    }

    private async Task SeedNotificationsApiScopeAsync()
    {
        await UpsertScopeAsync(
            name: "notifications",
            displayName: "Notifications API",
            resources: NotificationsScopeResources);
    }

    private async Task SeedAuditLogsApiScopeAsync()
    {
        await UpsertScopeAsync(
            name: "auditlogs",
            displayName: "Audit Logs API",
            resources: AuditLogsScopeResources);
    }

    private async Task SeedWorkflowsApiScopeAsync()
    {
        await UpsertScopeAsync(
            name: "workflows",
            displayName: "Workflows API",
            resources: WorkflowsScopeResources);
    }

    private async Task SeedIdentityInternalScopeAsync()
    {
        await UpsertScopeAsync(
            name: "identity-internal",
            displayName: "Identity Internal",
            resources: IdentityScopeResources);
    }

    private async Task SeedNotificationsInternalScopeAsync()
    {
        await UpsertScopeAsync(
            name: "notifications-internal",
            displayName: "Notifications Internal",
            resources: NotificationsScopeResources);
    }

    private async Task SeedTenantInternalScopeAsync()
    {
        await UpsertScopeAsync(
            name: "tenant-internal",
            displayName: "Tenant Internal",
            resources: TenantScopeResources);
    }

    private async Task SeedFeaturesCheckScopeAsync()
    {
        await UpsertScopeAsync(
            name: "features.check",
            displayName: "Features Check",
            resources: PermissionsScopeResources);
    }

    private async Task SeedCapabilityMetadataScopeAsync()
    {
        await UpsertScopeAsync(
            name: "capabilities.read",
            displayName: "Capability Metadata Read",
            resources: PermissionsScopeResources);
    }

    private async Task SeedEntitlementProvisioningScopeAsync()
    {
        await UpsertScopeAsync(
            name: "entitlements.provision",
            displayName: "Tenant Entitlement Provisioning",
            resources: PermissionsScopeResources);
    }

    private async Task SeedTenantServiceabilityScopeAsync()
    {
        await UpsertScopeAsync(
            name: "tenant-serviceability.read",
            displayName: "Tenant Serviceability Read",
            resources: TenantScopeResources);
    }

    /// <summary>
    /// Seeds the internal Permissions scope used by service identities to evaluate a subject's current
    /// authority. It targets the Permissions resource only and is still narrowed by Permissions' client allow-list.
    /// </summary>
    private async Task SeedPermissionsInternalScopeAsync()
    {
        await UpsertScopeAsync(
            name: "permissions-internal",
            displayName: "Permissions Internal",
            resources: PermissionsScopeResources);
    }

    /// <summary>
    /// Seeds Notifications' dedicated machine identity for internal Identity lookups at delivery time.
    /// It receives no browser scope.
    /// </summary>
    private async Task SeedNotificationsServiceClientAsync()
    {
        var clientId = _configuration["OpenIddict:Clients:NotificationsInternal:ClientId"]
            ?? "tenantwright-notifications-internal";
        var clientSecret = _configuration["OpenIddict:Clients:NotificationsInternal:Secret"];
        var isDevelopment = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            if (!isDevelopment)
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auth",
                    key: "OpenIddict:Clients:NotificationsInternal:Secret",
                    rule: "The Notifications recipient-resolution identity requires a client secret outside Development.",
                    fixHint: "Provide OpenIddict__Clients__NotificationsInternal__Secret through the deployment secret boundary.");
            }

            return;
        }

        var existing = await _applicationManager.FindByClientIdAsync(clientId);
        var descriptor = new OpenIddictApplicationDescriptor();
        if (existing != null) await _applicationManager.PopulateAsync(descriptor, existing);

        descriptor.ClientId = clientId;
        descriptor.DisplayName = _configuration["OpenIddict:Clients:NotificationsInternal:DisplayName"]
            ?? "TenantWright Notifications Internal Client";
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = OpenIddictConstants.ConsentTypes.Implicit;
        descriptor.ClientSecret = clientSecret;
        descriptor.Permissions.Clear();
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
        descriptor.Permissions.Add(
            OpenIddictConstants.Permissions.Prefixes.Scope + "identity-internal");
        MarkConfigurationManaged(descriptor.Properties);

        if (existing == null) await _applicationManager.CreateAsync(descriptor);
        else await _applicationManager.UpdateAsync(existing, descriptor);
    }

    private async Task CreateScopeIfMissingAsync(OpenIddictScopeDescriptor descriptor)
    {
        MarkConfigurationManaged(descriptor.Properties);
        if (await _scopeManager.FindByNameAsync(descriptor.Name!) != null) return;
        await _scopeManager.CreateAsync(descriptor);
    }

    private List<string> ReadConfiguredUris(string collectionKey, string singleKey, string fallback, bool allowFallback)
    {
        var values = _configuration
            .GetSection(collectionKey)
            .GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToList();

        if (values.Count == 0)
        {
            var singleValue = _configuration[singleKey];
            if (string.IsNullOrWhiteSpace(singleValue))
            {
                if (!allowFallback)
                {
                    throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-auth",
                        key: $"{collectionKey}|{singleKey}",
                        rule: "Explicit redirect URI configuration is required outside Development.",
                        fixHint: $"Set '{collectionKey}' or '{singleKey}' to the approved callback URI.");
                }

                values = new List<string> { fallback };
            }
            else
            {
                values = singleValue
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureConfiguredUrisAreValid(string key, IEnumerable<string> uris)
    {
        var result = _validator.ValidateUris(uris);
        if (!result.IsValid)
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-auth",
                key: key,
                rule: result.Message ?? "Configured redirect URIs are invalid.",
                fixHint: "Use exact HTTPS callback URIs without fragments, user information, or wildcards.");
        }
    }

    private static void MarkConfigurationManaged(Dictionary<string, System.Text.Json.JsonElement> properties)
    {
        properties["tenantwright:management-source"] = System.Text.Json.JsonSerializer.SerializeToElement(OpenIddictManagementSources.Configuration);
        if (!properties.ContainsKey("tenantwright:management-version"))
        {
            properties["tenantwright:management-version"] = System.Text.Json.JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N"));
        }
    }

    private async Task UpsertScopeAsync(string name, string displayName, IEnumerable<string> resources)
    {
        var existing = await _scopeManager.FindByNameAsync(name);

        var descriptor = new OpenIddictScopeDescriptor();
        if (existing != null)
        {
            await _scopeManager.PopulateAsync(descriptor, existing);
        }

        descriptor.Name = name;
        descriptor.DisplayName = displayName;

        descriptor.Resources.Clear();
        foreach (var resource in resources.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            descriptor.Resources.Add(resource);
        }
        MarkConfigurationManaged(descriptor.Properties);

        if (existing == null)
        {
            await _scopeManager.CreateAsync(descriptor);
        }
        else
        {
            await _scopeManager.UpdateAsync(existing, descriptor);
        }
    }
}
