using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auth.Caching;
using Auth.Models;
using Auth.TokenMetadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Managers;

public sealed class OpenIddictManagementManager
{
    private const string ManagementSourceProperty = "tenantwright:management-source";
    private const string ManagementVersionProperty = "tenantwright:management-version";
    private const string SecretLastRotatedAtProperty = "tenantwright:secret-last-rotated-at-utc";
    private const string SecretLastRotatedByProperty = "tenantwright:secret-last-rotated-by";
    private const string SecretRevokedAtProperty = "tenantwright:secret-revoked-at-utc";
    private const string SecretRevokedByProperty = "tenantwright:secret-revoked-by";
    private const string GrantTypePermissionPrefix = "gt:";
    private const string EndpointPermissionPrefix = "ept:";
    private const string ScopePermissionPrefix = "scp:";
    private const string ResponseTypePermissionPrefix = "rst:";

    private static readonly Meter Meter = new("TenantWright.Auth.OpenIddictManagement", "1.0.0");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("tenantwright.auth.openiddict.management.operations");
    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>("tenantwright.auth.openiddict.management.rejections");

    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictApplicationCache _applicationCache;
    private readonly IOpenIddictScopeCache _scopeCache;
    private readonly ITokenMetadataInvalidationService _tokenMetadataInvalidationService;
    private readonly OpenIddictManagementValidator _validator;
    private readonly ILogger<OpenIddictManagementManager> _logger;
    private readonly string _authority;

    public OpenIddictManagementManager(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictApplicationCache applicationCache,
        IOpenIddictScopeCache scopeCache,
        ITokenMetadataInvalidationService tokenMetadataInvalidationService,
        OpenIddictManagementValidator validator,
        IConfiguration configuration,
        ILogger<OpenIddictManagementManager> logger)
    {
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
        _authorizationManager = authorizationManager;
        _tokenManager = tokenManager;
        _applicationCache = applicationCache;
        _scopeCache = scopeCache;
        _tokenMetadataInvalidationService = tokenMetadataInvalidationService;
        _validator = validator;
        _logger = logger;
        _authority = (configuration["OpenIddict:Issuer"] ?? configuration["AuthServer:Authority"] ?? string.Empty).TrimEnd('/');
    }

    public async Task<OpenIddictManagementPage<OpenIddictApplicationModel>> GetApplicationsAsync(
        string? search,
        string? clientType,
        bool? configurationManaged,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken)
    {
        ValidatePage(skipCount, maxResultCount);
        search = Normalize(search);
        clientType = Normalize(clientType);
        if (clientType != null && clientType is not (ClientTypes.Public or ClientTypes.Confidential))
        {
            Reject("list_applications", "invalid_client_type", "Client type filter is invalid.");
        }

        var rows = new List<OpenIddictApplicationModel>();
        await foreach (var application in _applicationManager.ListAsync(cancellationToken: cancellationToken))
        {
            var model = await BuildApplicationModelAsync(application, cancellationToken);
            if (clientType != null && !string.Equals(model.ClientType, clientType, StringComparison.Ordinal))
            {
                continue;
            }
            if (configurationManaged.HasValue &&
                configurationManaged.Value != string.Equals(model.Management.Source, OpenIddictManagementSources.Configuration, StringComparison.Ordinal))
            {
                continue;
            }
            if (search != null && !ApplicationMatches(model, search))
            {
                continue;
            }
            rows.Add(model);
        }

        rows.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.ClientId, right.ClientId));
        var page = Page(rows, skipCount, maxResultCount);
        await ApplyLastTokenIssuedAsync(page.Items, cancellationToken);
        return page;
    }

    public async Task<OpenIddictApplicationModel> GetApplicationAsync(string clientId, CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        var cached = await _applicationCache.GetByClientIdAsync(clientId, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw NotFound("application_not_found", "The OAuth client does not exist.");
        var model = await BuildApplicationModelAsync(application, cancellationToken);
        await ApplyLastTokenIssuedAsync([model], cancellationToken);
        await _applicationCache.SetAsync(model, cancellationToken);
        return model;
    }

    public async Task<OpenIddictApplicationSecretResult> CreateApplicationAsync(
        OpenIddictApplicationCreateInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = _validator.ValidateApplication(
            input.ClientId,
            input.ClientType,
            input.Permissions,
            input.RedirectUris,
            input.PostLogoutRedirectUris,
            isCreate: true);
        EnsureValid("create_application", validation);

        if (await _applicationManager.FindByClientIdAsync(input.ClientId, cancellationToken) != null)
        {
            Reject("create_application", "client_id_exists", "An OAuth client with this client ID already exists.", StatusCodes.Status409Conflict);
        }

        var secret = string.Equals(input.ClientType, ClientTypes.Confidential, StringComparison.Ordinal)
            ? GenerateSecret()
            : null;
        var descriptor = CreateApplicationDescriptor(input, secret, actor);

        try
        {
            await _applicationManager.CreateAsync(descriptor, cancellationToken);
        }
        catch (Exception exception) when (IsCreateConflictException(exception))
        {
            if (await _applicationManager.FindByClientIdAsync(input.ClientId, cancellationToken) != null)
            {
                Reject("create_application", "client_id_exists", "An OAuth client with this client ID already exists.", StatusCodes.Status409Conflict);
            }

            throw;
        }

        var created = await _applicationManager.FindByClientIdAsync(input.ClientId, cancellationToken)
            ?? throw new InvalidOperationException("The created OAuth client could not be loaded.");
        var model = await BuildApplicationModelAsync(created, cancellationToken);
        await _applicationCache.SetAsync(model, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateClientAsync(input.ClientId, cancellationToken);
        TrackOperation("create_application", "success");
        _logger.LogInformation("OpenIddict application created. ClientId={ClientId}, Actor={Actor}", input.ClientId, actor);

        return new OpenIddictApplicationSecretResult
        {
            ClientSecret = secret,
            Application = model,
            Integration = BuildIntegration(model)
        };
    }

    public async Task<OpenIddictApplicationModel> UpdateApplicationAsync(
        string clientId,
        OpenIddictApplicationUpdateInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateClientId(clientId);
        if (!string.Equals(clientId, input.ClientId, StringComparison.Ordinal))
        {
            Reject("update_application", "client_id_mismatch", "Route client ID must match the request body.");
        }
        EnsureMutableClient(clientId, "update_application");
        EnsureValid("update_application", _validator.ValidateApplication(
            input.ClientId,
            input.ClientType,
            input.Permissions,
            input.RedirectUris,
            input.PostLogoutRedirectUris,
            isCreate: false));

        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw NotFound("application_not_found", "The OAuth client does not exist.");
        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        EnsureConcurrency("update_application", input.ConcurrencyToken, ReadConcurrencyToken(descriptor));
        if (!string.Equals(descriptor.ClientType, input.ClientType, StringComparison.Ordinal))
        {
            Reject(
                "update_application",
                "client_type_immutable",
                "Client type cannot be changed after an OAuth client is created.",
                StatusCodes.Status409Conflict,
                "Create a new client with the required type so credential policy cannot be bypassed through an ordinary update.");
        }

        descriptor.DisplayName = Normalize(input.DisplayName);
        descriptor.ClientType = input.ClientType;
        descriptor.ConsentType = input.ConsentType;
        ReplacePermissions(descriptor, input.Permissions!);
        ReplaceUris(descriptor.RedirectUris, input.RedirectUris!);
        ReplaceUris(descriptor.PostLogoutRedirectUris, input.PostLogoutRedirectUris!);
        ApplyPkceRequirement(descriptor, input.ClientType);
        SetProperty(descriptor.Properties, ManagementVersionProperty, NewConcurrencyToken());

        await UpdateApplicationDescriptorAsync(application, descriptor, "update_application", cancellationToken);
        var model = await BuildApplicationModelAsync(application, cancellationToken);
        await _applicationCache.SetAsync(model, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateClientAsync(clientId, cancellationToken);
        TrackOperation("update_application", "success");
        _logger.LogInformation("OpenIddict application updated. ClientId={ClientId}, Actor={Actor}", clientId, actor);
        return model;
    }

    public async Task<OpenIddictApplicationSecretResult> RotateSecretAsync(
        string clientId,
        string expectedConcurrencyToken,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        EnsureMutableClient(clientId, "rotate_secret");
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw NotFound("application_not_found", "The OAuth client does not exist.");
        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        EnsureConcurrency("rotate_secret", expectedConcurrencyToken, ReadConcurrencyToken(descriptor));
        if (!string.Equals(descriptor.ClientType, ClientTypes.Confidential, StringComparison.Ordinal))
        {
            Reject("rotate_secret", "public_client_secret", "Public clients cannot have a client secret. They use PKCE instead.");
        }

        var secret = GenerateSecret();
        descriptor.ClientSecret = secret;
        SetProperty(descriptor.Properties, SecretLastRotatedAtProperty, DateTimeOffset.UtcNow);
        SetProperty(descriptor.Properties, SecretLastRotatedByProperty, actor);
        SetProperty(descriptor.Properties, SecretRevokedAtProperty, (DateTimeOffset?)null);
        SetProperty(descriptor.Properties, SecretRevokedByProperty, (string?)null);
        SetProperty(descriptor.Properties, ManagementVersionProperty, NewConcurrencyToken());
        await UpdateApplicationDescriptorAsync(application, descriptor, "rotate_secret", cancellationToken);

        var model = await BuildApplicationModelAsync(application, cancellationToken);
        await _applicationCache.SetAsync(model, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateClientAsync(clientId, cancellationToken);
        TrackOperation("rotate_secret", "success");
        _logger.LogWarning("OpenIddict application secret rotated. ClientId={ClientId}, Actor={Actor}", clientId, actor);
        return new OpenIddictApplicationSecretResult
        {
            ClientSecret = secret,
            Application = model,
            Integration = BuildIntegration(model)
        };
    }

    public async Task<OpenIddictApplicationModel> RevokeSecretAsync(
        string clientId,
        string expectedConcurrencyToken,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        EnsureMutableClient(clientId, "revoke_secret");
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw NotFound("application_not_found", "The OAuth client does not exist.");
        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        EnsureConcurrency("revoke_secret", expectedConcurrencyToken, ReadConcurrencyToken(descriptor));
        if (!string.Equals(descriptor.ClientType, ClientTypes.Confidential, StringComparison.Ordinal))
        {
            Reject("revoke_secret", "public_client_secret", "Public clients do not have a client secret.");
        }

        // OpenIddict rejects confidential clients without a credential. Replacing the
        // current secret with an unrevealed random value invalidates every known secret
        // while preserving the confidential-client invariant. A later rotation replaces
        // this tombstone credential and reveals the new value exactly once.
        descriptor.ClientSecret = GenerateSecret();
        SetProperty(descriptor.Properties, SecretLastRotatedAtProperty, (DateTimeOffset?)null);
        SetProperty(descriptor.Properties, SecretLastRotatedByProperty, (string?)null);
        SetProperty(descriptor.Properties, SecretRevokedAtProperty, DateTimeOffset.UtcNow);
        SetProperty(descriptor.Properties, SecretRevokedByProperty, actor);
        SetProperty(descriptor.Properties, ManagementVersionProperty, NewConcurrencyToken());
        await UpdateApplicationDescriptorAsync(application, descriptor, "revoke_secret", cancellationToken);

        var model = await BuildApplicationModelAsync(application, cancellationToken);
        await _applicationCache.SetAsync(model, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateClientAsync(clientId, cancellationToken);
        TrackOperation("revoke_secret", "success");
        _logger.LogWarning("OpenIddict application secret revoked. ClientId={ClientId}, Actor={Actor}", clientId, actor);
        return model;
    }

    public async Task DeleteApplicationAsync(
        string clientId,
        string? currentRequestClientId,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        EnsureMutableClient(clientId, "delete_application");
        if (string.Equals(clientId, currentRequestClientId, StringComparison.Ordinal))
        {
            Reject("delete_application", "self_lockout", "The OAuth client used by the current request cannot delete itself.", StatusCodes.Status409Conflict);
        }

        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw NotFound("application_not_found", "The OAuth client does not exist.");
        await _applicationManager.DeleteAsync(application, cancellationToken);
        await _applicationCache.RemoveAsync(clientId, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateClientAsync(clientId, cancellationToken);
        TrackOperation("delete_application", "success");
        _logger.LogWarning("OpenIddict application deleted. ClientId={ClientId}, Actor={Actor}", clientId, actor);
    }

    public async Task<OpenIddictApplicationImpactModel> GetApplicationImpactAsync(
        string clientId,
        string? currentRequestClientId,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw NotFound("application_not_found", "The OAuth client does not exist.");
        var applicationId = await _applicationManager.GetIdAsync(application, cancellationToken) ?? string.Empty;
        var authorizationScopes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        long activeAuthorizationCount = 0;

        await foreach (var authorization in _authorizationManager.FindByApplicationIdAsync(applicationId, cancellationToken))
        {
            if (!string.Equals(await _authorizationManager.GetStatusAsync(authorization, cancellationToken), Statuses.Valid, StringComparison.Ordinal))
            {
                continue;
            }
            activeAuthorizationCount++;
            var authorizationId = await _authorizationManager.GetIdAsync(authorization, cancellationToken);
            if (authorizationId != null)
            {
                authorizationScopes[authorizationId] = [.. await _authorizationManager.GetScopesAsync(authorization, cancellationToken)];
            }
        }

        var now = DateTimeOffset.UtcNow;
        long activeTokenCount = 0;
        var tokensByScope = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var token in _tokenManager.FindByApplicationIdAsync(applicationId, cancellationToken))
        {
            var status = await _tokenManager.GetStatusAsync(token, cancellationToken);
            var expiration = await _tokenManager.GetExpirationDateAsync(token, cancellationToken);
            if (!string.Equals(status, Statuses.Valid, StringComparison.Ordinal) || expiration <= now)
            {
                continue;
            }
            activeTokenCount++;
            var authorizationId = await _tokenManager.GetAuthorizationIdAsync(token, cancellationToken);
            if (authorizationId == null || !authorizationScopes.TryGetValue(authorizationId, out var scopes))
            {
                continue;
            }
            foreach (var scope in scopes)
            {
                tokensByScope[scope] = tokensByScope.GetValueOrDefault(scope) + 1;
            }
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        return new OpenIddictApplicationImpactModel
        {
            ClientId = clientId,
            ActiveAuthorizationCount = activeAuthorizationCount,
            ActiveTokenCount = activeTokenCount,
            ActiveTokenCountByScope = tokensByScope,
            ReferencedScopes = DecodePermissions(descriptor.Permissions).Scopes,
            IsCurrentRequestClient = string.Equals(clientId, currentRequestClientId, StringComparison.Ordinal)
        };
    }

    public async Task<OpenIddictManagementPage<OpenIddictScopeModel>> GetScopesAsync(
        string? search,
        bool? configurationManaged,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken)
    {
        ValidatePage(skipCount, maxResultCount);
        search = Normalize(search);
        var rows = new List<OpenIddictScopeModel>();
        await foreach (var scope in _scopeManager.ListAsync(cancellationToken: cancellationToken))
        {
            var model = await BuildScopeModelAsync(scope, includeReferences: false, cancellationToken);
            if (configurationManaged.HasValue &&
                configurationManaged.Value != string.Equals(model.Management.Source, OpenIddictManagementSources.Configuration, StringComparison.Ordinal))
            {
                continue;
            }
            if (search != null &&
                !(model.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                  (model.DisplayName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                  model.Resources.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }
            rows.Add(model);
        }
        rows.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return Page(rows, skipCount, maxResultCount);
    }

    public async Task<OpenIddictScopeModel> GetScopeAsync(string name, CancellationToken cancellationToken)
    {
        ValidateScopeName(name);
        var scope = await _scopeManager.FindByNameAsync(name, cancellationToken)
            ?? throw NotFound("scope_not_found", "The OAuth scope does not exist.");
        var model = await BuildScopeModelAsync(scope, includeReferences: true, cancellationToken);
        await _scopeCache.SetAsync(model, cancellationToken);
        return model;
    }

    public async Task<OpenIddictScopeModel> CreateScopeAsync(
        OpenIddictScopeCreateInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureValid("create_scope", _validator.ValidateScope(input.Name, input.Resources, isCreate: true));
        if (await _scopeManager.FindByNameAsync(input.Name, cancellationToken) != null)
        {
            Reject("create_scope", "scope_exists", "An OAuth scope with this name already exists.", StatusCodes.Status409Conflict);
        }

        var descriptor = new OpenIddictScopeDescriptor
        {
            Name = input.Name,
            DisplayName = Normalize(input.DisplayName)
        };
        ReplaceValues(descriptor.Resources, input.Resources!);
        SetProperty(descriptor.Properties, ManagementSourceProperty, OpenIddictManagementSources.Console);
        SetProperty(descriptor.Properties, ManagementVersionProperty, NewConcurrencyToken());
        try
        {
            await _scopeManager.CreateAsync(descriptor, cancellationToken);
        }
        catch (Exception exception) when (IsCreateConflictException(exception))
        {
            if (await _scopeManager.FindByNameAsync(input.Name, cancellationToken) != null)
            {
                Reject("create_scope", "scope_exists", "An OAuth scope with this name already exists.", StatusCodes.Status409Conflict);
            }

            throw;
        }

        var created = await _scopeManager.FindByNameAsync(input.Name, cancellationToken)
            ?? throw new InvalidOperationException("The created OAuth scope could not be loaded.");
        var model = await BuildScopeModelAsync(created, includeReferences: false, cancellationToken);
        await _scopeCache.SetAsync(model, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateScopeAsync(input.Name, cancellationToken);
        TrackOperation("create_scope", "success");
        _logger.LogInformation("OpenIddict scope created. ScopeName={ScopeName}, Actor={Actor}", input.Name, actor);
        return model;
    }

    public async Task<OpenIddictScopeModel> UpdateScopeAsync(
        string name,
        OpenIddictScopeUpdateInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateScopeName(name);
        if (!string.Equals(name, input.Name, StringComparison.Ordinal))
        {
            Reject("update_scope", "scope_name_mismatch", "Route scope name must match the request body.");
        }
        EnsureMutableScope(name, "update_scope");
        EnsureValid("update_scope", _validator.ValidateScope(name, input.Resources, isCreate: false));

        var scope = await _scopeManager.FindByNameAsync(name, cancellationToken)
            ?? throw NotFound("scope_not_found", "The OAuth scope does not exist.");
        var descriptor = new OpenIddictScopeDescriptor();
        await _scopeManager.PopulateAsync(descriptor, scope, cancellationToken);
        EnsureConcurrency("update_scope", input.ConcurrencyToken, ReadConcurrencyToken(descriptor));
        descriptor.DisplayName = Normalize(input.DisplayName);
        ReplaceValues(descriptor.Resources, input.Resources!);
        SetProperty(descriptor.Properties, ManagementVersionProperty, NewConcurrencyToken());

        try
        {
            await _scopeManager.UpdateAsync(scope, descriptor, cancellationToken);
        }
        catch (Exception exception) when (IsConcurrencyException(exception))
        {
            Reject("update_scope", "concurrency_conflict", "The OAuth scope changed after it was loaded. Reload and try again.", StatusCodes.Status409Conflict);
        }

        var model = await BuildScopeModelAsync(scope, includeReferences: false, cancellationToken);
        await _scopeCache.SetAsync(model, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateScopeAsync(name, cancellationToken);
        TrackOperation("update_scope", "success");
        _logger.LogInformation("OpenIddict scope updated. ScopeName={ScopeName}, Actor={Actor}", name, actor);
        return model;
    }

    public async Task DeleteScopeAsync(string name, string actor, CancellationToken cancellationToken)
    {
        ValidateScopeName(name);
        EnsureMutableScope(name, "delete_scope");
        var scope = await _scopeManager.FindByNameAsync(name, cancellationToken)
            ?? throw NotFound("scope_not_found", "The OAuth scope does not exist.");
        await _scopeManager.DeleteAsync(scope, cancellationToken);
        await _scopeCache.RemoveAsync(name, cancellationToken);
        await _tokenMetadataInvalidationService.InvalidateScopeAsync(name, cancellationToken);
        TrackOperation("delete_scope", "success");
        _logger.LogWarning("OpenIddict scope deleted. ScopeName={ScopeName}, Actor={Actor}", name, actor);
    }

    public async Task<OpenIddictManagementMetadataModel> GetMetadataAsync(CancellationToken cancellationToken)
    {
        var scopes = new List<OpenIddictMetadataOptionModel>();
        await foreach (var scope in _scopeManager.ListAsync(cancellationToken: cancellationToken))
        {
            var name = await _scopeManager.GetNameAsync(scope, cancellationToken);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            scopes.Add(new OpenIddictMetadataOptionModel
            {
                Value = name,
                DisplayName = await _scopeManager.GetDisplayNameAsync(scope, cancellationToken) ?? name
            });
        }
        scopes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName));

        return new OpenIddictManagementMetadataModel
        {
            Authority = _authority,
            ClientTypes =
            [
                Option(ClientTypes.Public, "Public"),
                Option(ClientTypes.Confidential, "Confidential")
            ],
            ConsentTypes =
            [
                Option(ConsentTypes.Explicit, "Explicit consent"),
                Option(ConsentTypes.Implicit, "Implicit consent"),
                Option(ConsentTypes.External, "External consent"),
                Option(ConsentTypes.Systematic, "Systematic consent")
            ],
            GrantTypes =
            [
                Grant(GrantTypes.AuthorizationCode, "Authorization code", ClientTypes.Public, ClientTypes.Confidential),
                Grant(GrantTypes.ClientCredentials, "Client credentials", ClientTypes.Confidential),
                Grant(GrantTypes.RefreshToken, "Refresh token", ClientTypes.Public, ClientTypes.Confidential)
            ],
            Endpoints =
            [
                Option("authorization", "Authorization endpoint"),
                Option("token", "Token endpoint"),
                Option("end_session", "End-session endpoint"),
                Option("revocation", "Revocation endpoint")
            ],
            ResponseTypes = [Option(ResponseTypes.Code, "Authorization code")],
            Scopes = scopes,
            Templates = BuildTemplates()
        };
    }

    private static OpenIddictApplicationDescriptor CreateApplicationDescriptor(
        OpenIddictApplicationCreateInput input,
        string? secret,
        string actor)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = input.ClientId.Trim(),
            ClientSecret = secret,
            DisplayName = Normalize(input.DisplayName),
            ClientType = input.ClientType,
            ConsentType = input.ConsentType
        };
        ReplacePermissions(descriptor, input.Permissions!);
        ReplaceUris(descriptor.RedirectUris, input.RedirectUris!);
        ReplaceUris(descriptor.PostLogoutRedirectUris, input.PostLogoutRedirectUris!);
        ApplyPkceRequirement(descriptor, input.ClientType);
        SetProperty(descriptor.Properties, ManagementSourceProperty, OpenIddictManagementSources.Console);
        SetProperty(descriptor.Properties, ManagementVersionProperty, NewConcurrencyToken());
        if (secret != null)
        {
            SetProperty(descriptor.Properties, SecretLastRotatedAtProperty, DateTimeOffset.UtcNow);
            SetProperty(descriptor.Properties, SecretLastRotatedByProperty, actor);
        }
        return descriptor;
    }

    private async Task<OpenIddictApplicationModel> BuildApplicationModelAsync(object application, CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        var clientId = descriptor.ClientId ?? string.Empty;
        var source = ReadStringProperty(descriptor.Properties, ManagementSourceProperty) ??
            (_validator.IsConfigurationManagedClient(clientId) ? OpenIddictManagementSources.Configuration : OpenIddictManagementSources.Console);
        var isMutable = string.Equals(source, OpenIddictManagementSources.Console, StringComparison.Ordinal);
        var requirements = descriptor.Requirements.Order(StringComparer.Ordinal).ToArray();
        return new OpenIddictApplicationModel
        {
            Id = await _applicationManager.GetIdAsync(application, cancellationToken) ?? string.Empty,
            ClientId = clientId,
            DisplayName = descriptor.DisplayName,
            ClientType = descriptor.ClientType ?? ClientTypes.Public,
            ConsentType = descriptor.ConsentType ?? ConsentTypes.Explicit,
            ConcurrencyToken = ReadConcurrencyToken(descriptor),
            Permissions = DecodePermissions(descriptor.Permissions),
            RedirectUris = descriptor.RedirectUris.Select(uri => uri.ToString()).Order(StringComparer.Ordinal).ToArray(),
            PostLogoutRedirectUris = descriptor.PostLogoutRedirectUris.Select(uri => uri.ToString()).Order(StringComparer.Ordinal).ToArray(),
            Requirements = requirements,
            RequiresPkce = requirements.Contains(Requirements.Features.ProofKeyForCodeExchange, StringComparer.Ordinal),
            Credential = new OpenIddictCredentialMetadataModel
            {
                IsConfigured = !string.IsNullOrWhiteSpace(descriptor.ClientSecret) &&
                    ReadDateTimeOffsetProperty(descriptor.Properties, SecretRevokedAtProperty) == null,
                LastRotatedAtUtc = ReadDateTimeOffsetProperty(descriptor.Properties, SecretLastRotatedAtProperty),
                LastRotatedByActor = ReadStringProperty(descriptor.Properties, SecretLastRotatedByProperty),
                RevokedAtUtc = ReadDateTimeOffsetProperty(descriptor.Properties, SecretRevokedAtProperty),
                RevokedByActor = ReadStringProperty(descriptor.Properties, SecretRevokedByProperty)
            },
            Management = new OpenIddictManagementOwnershipModel
            {
                Source = source,
                IsMutable = isMutable,
                BlockedReason = isMutable ? null : "This record is reconciled from deployment configuration. Change its owning configuration and redeploy."
            }
        };
    }

    private async Task<OpenIddictScopeModel> BuildScopeModelAsync(
        object scope,
        bool includeReferences,
        CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictScopeDescriptor();
        await _scopeManager.PopulateAsync(descriptor, scope, cancellationToken);
        var name = descriptor.Name ?? string.Empty;
        var source = ReadStringProperty(descriptor.Properties, ManagementSourceProperty) ??
            (_validator.IsConfigurationManagedScope(name) ? OpenIddictManagementSources.Configuration : OpenIddictManagementSources.Console);
        var isMutable = string.Equals(source, OpenIddictManagementSources.Console, StringComparison.Ordinal);
        return new OpenIddictScopeModel
        {
            Id = await _scopeManager.GetIdAsync(scope, cancellationToken) ?? string.Empty,
            Name = name,
            DisplayName = descriptor.DisplayName,
            ConcurrencyToken = ReadConcurrencyToken(descriptor),
            Resources = descriptor.Resources.Order(StringComparer.Ordinal).ToArray(),
            Management = new OpenIddictManagementOwnershipModel
            {
                Source = source,
                IsMutable = isMutable,
                BlockedReason = isMutable ? null : "This scope is reconciled from deployment configuration. Change its owning configuration and redeploy."
            },
            ReferencingClientIds = includeReferences
                ? await GetReferencingClientIdsAsync(name, cancellationToken)
                : []
        };
    }

    private async Task<string[]> GetReferencingClientIdsAsync(string scopeName, CancellationToken cancellationToken)
    {
        var clients = new List<string>();
        await foreach (var application in _applicationManager.ListAsync(cancellationToken: cancellationToken))
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
            if (DecodePermissions(descriptor.Permissions).Scopes.Contains(scopeName, StringComparer.Ordinal) && descriptor.ClientId != null)
            {
                clients.Add(descriptor.ClientId);
            }
        }
        return clients.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task ApplyLastTokenIssuedAsync(List<OpenIddictApplicationModel> applications, CancellationToken cancellationToken)
    {
        if (applications.Count == 0)
        {
            return;
        }
        var byApplicationId = applications.ToDictionary(application => application.Id, StringComparer.Ordinal);
        await foreach (var token in _tokenManager.ListAsync(cancellationToken: cancellationToken))
        {
            var applicationId = await _tokenManager.GetApplicationIdAsync(token, cancellationToken);
            if (applicationId == null || !byApplicationId.TryGetValue(applicationId, out var application))
            {
                continue;
            }
            var creationDate = await _tokenManager.GetCreationDateAsync(token, cancellationToken);
            if (creationDate.HasValue &&
                (!application.LastTokenIssuedAtUtc.HasValue || creationDate > application.LastTokenIssuedAtUtc))
            {
                application.LastTokenIssuedAtUtc = creationDate;
            }
        }
    }

    private static bool ApplicationMatches(OpenIddictApplicationModel model, string search)
    {
        return model.ClientId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (model.DisplayName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               model.RedirectUris.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               model.PostLogoutRedirectUris.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               model.Permissions.Scopes.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReplacePermissions(OpenIddictApplicationDescriptor descriptor, OpenIddictApplicationPermissionInput permissions)
    {
        descriptor.Permissions.Clear();
        AddPrefixed(descriptor.Permissions, GrantTypePermissionPrefix, permissions.GrantTypes!);
        AddPrefixed(descriptor.Permissions, EndpointPermissionPrefix, permissions.Endpoints!);
        AddPrefixed(descriptor.Permissions, ScopePermissionPrefix, permissions.Scopes!);
        AddPrefixed(descriptor.Permissions, ResponseTypePermissionPrefix, permissions.ResponseTypes!);
        foreach (var permission in permissions.AdditionalPermissions!.Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            descriptor.Permissions.Add(permission);
        }
    }

    private static OpenIddictApplicationPermissionModel DecodePermissions(IEnumerable<string> permissions)
    {
        var model = new OpenIddictApplicationPermissionModel();
        var grantTypes = new List<string>();
        var endpoints = new List<string>();
        var scopes = new List<string>();
        var responseTypes = new List<string>();
        var additional = new List<string>();
        foreach (var permission in permissions)
        {
            if (TryStrip(permission, GrantTypePermissionPrefix, out var value)) grantTypes.Add(value);
            else if (TryStrip(permission, EndpointPermissionPrefix, out value)) endpoints.Add(value);
            else if (TryStrip(permission, ScopePermissionPrefix, out value)) scopes.Add(value);
            else if (TryStrip(permission, ResponseTypePermissionPrefix, out value)) responseTypes.Add(value);
            else additional.Add(permission);
        }
        model.GrantTypes = grantTypes.Order(StringComparer.Ordinal).ToArray();
        model.Endpoints = endpoints.Order(StringComparer.Ordinal).ToArray();
        model.Scopes = scopes.Order(StringComparer.Ordinal).ToArray();
        model.ResponseTypes = responseTypes.Order(StringComparer.Ordinal).ToArray();
        model.AdditionalPermissions = additional.Order(StringComparer.Ordinal).ToArray();
        return model;
    }

    private static void AddPrefixed(HashSet<string> target, string prefix, IEnumerable<string> values)
    {
        foreach (var value in values.Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            target.Add(prefix + value);
        }
    }

    private static bool TryStrip(string value, string prefix, out string result)
    {
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            result = value[prefix.Length..];
            return true;
        }
        result = string.Empty;
        return false;
    }

    private static void ReplaceUris(HashSet<Uri> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values.Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            target.Add(new Uri(value, UriKind.Absolute));
        }
    }

    private static void ReplaceValues(HashSet<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values.Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            target.Add(value);
        }
    }

    private static void ApplyPkceRequirement(OpenIddictApplicationDescriptor descriptor, string clientType)
    {
        if (string.Equals(clientType, ClientTypes.Public, StringComparison.Ordinal))
        {
            descriptor.ClientSecret = null;
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        }
        else
        {
            descriptor.Requirements.Remove(Requirements.Features.ProofKeyForCodeExchange);
        }
    }

    private async Task UpdateApplicationDescriptorAsync(
        object application,
        OpenIddictApplicationDescriptor descriptor,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);
        }
        catch (Exception exception) when (IsConcurrencyException(exception))
        {
            Reject(operation, "concurrency_conflict", "The OAuth client changed after it was loaded. Reload and try again.", StatusCodes.Status409Conflict);
        }
    }

    private static bool IsConcurrencyException(Exception exception)
    {
        return exception is OpenIddictExceptions.ConcurrencyException ||
               (exception.InnerException != null && IsConcurrencyException(exception.InnerException));
    }

    private static bool IsCreateConflictException(Exception exception)
    {
        return exception is OpenIddictExceptions.ValidationException or DbUpdateException ||
               (exception.InnerException != null && IsCreateConflictException(exception.InnerException));
    }

    private static string ReadConcurrencyToken(OpenIddictApplicationDescriptor descriptor)
    {
        return ReadStringProperty(descriptor.Properties, ManagementVersionProperty) ?? ComputeFallbackToken(
            descriptor.ClientId,
            descriptor.DisplayName,
            descriptor.ClientType,
            descriptor.ConsentType,
            descriptor.Permissions,
            descriptor.RedirectUris.Select(uri => uri.ToString()),
            descriptor.PostLogoutRedirectUris.Select(uri => uri.ToString()));
    }

    private static string ReadConcurrencyToken(OpenIddictScopeDescriptor descriptor)
    {
        return ReadStringProperty(descriptor.Properties, ManagementVersionProperty) ?? ComputeFallbackToken(
            descriptor.Name,
            descriptor.DisplayName,
            descriptor.Resources);
    }

    private static string ComputeFallbackToken(params object?[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)));
        return WebEncoders.Base64UrlEncode(bytes);
    }

    private static string NewConcurrencyToken() => Guid.NewGuid().ToString("N");
    private static string GenerateSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static void SetProperty<T>(Dictionary<string, JsonElement> properties, string name, T value)
    {
        if (value == null)
        {
            properties.Remove(name);
            return;
        }
        properties[name] = JsonSerializer.SerializeToElement(value);
    }

    private static string? ReadStringProperty(Dictionary<string, JsonElement> properties, string name)
    {
        return properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadDateTimeOffsetProperty(Dictionary<string, JsonElement> properties, string name)
    {
        return properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var result)
            ? result
            : null;
    }

    private static OpenIddictManagementPage<T> Page<T>(List<T> rows, int skipCount, int maxResultCount)
    {
        var totalCount = rows.Count;
        return new OpenIddictManagementPage<T>
        {
            SkipCount = skipCount,
            MaxResultCount = maxResultCount,
            TotalCount = totalCount,
            HasMore = skipCount + maxResultCount < totalCount,
            Items = rows.Skip(skipCount).Take(maxResultCount).ToList()
        };
    }

    private static void ValidatePage(int skipCount, int maxResultCount)
    {
        if (skipCount is < 0 or > 100_000 || maxResultCount is < 1 or > 100)
        {
            throw new OpenIddictManagementException(
                StatusCodes.Status400BadRequest,
                "invalid_page",
                "Page bounds must use skipCount 0-100000 and maxResultCount 1-100.");
        }
    }

    private static void ValidateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 100)
        {
            throw new OpenIddictManagementException(StatusCodes.Status400BadRequest, "invalid_client_id", "A valid client ID is required.");
        }
    }

    private static void ValidateScopeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new OpenIddictManagementException(StatusCodes.Status400BadRequest, "invalid_scope_name", "A valid scope name is required.");
        }
    }

    private void EnsureMutableClient(string clientId, string operation)
    {
        if (_validator.IsConfigurationManagedClient(clientId))
        {
            Reject(
                operation,
                "configuration_managed",
                "Configuration-managed clients cannot be changed through the console.",
                StatusCodes.Status409Conflict,
                "Change the owning application or Kubernetes secret/configuration and redeploy the Auth service.");
        }
    }

    private void EnsureMutableScope(string name, string operation)
    {
        if (_validator.IsConfigurationManagedScope(name))
        {
            Reject(
                operation,
                "configuration_managed",
                "Configuration-managed scopes cannot be changed through the console.",
                StatusCodes.Status409Conflict,
                "Change the Auth service seed configuration and redeploy.");
        }
    }

    private static void EnsureConcurrency(string operation, string expected, string current)
    {
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, current, StringComparison.Ordinal))
        {
            Rejections.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("reason", "concurrency_conflict"));
            throw new OpenIddictManagementException(
                StatusCodes.Status409Conflict,
                "concurrency_conflict",
                "The OAuth record changed after it was loaded. Reload and try again.");
        }
    }

    private static void EnsureValid(string operation, OpenIddictInputValidationResult validation)
    {
        if (!validation.IsValid)
        {
            Reject(operation, validation.Reason ?? "validation", validation.Message ?? "The OAuth configuration is invalid.");
        }
    }

    private static void Reject(
        string operation,
        string reason,
        string message,
        int statusCode = StatusCodes.Status400BadRequest,
        string? remediation = null)
    {
        Rejections.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("reason", reason));
        throw new OpenIddictManagementException(statusCode, reason, message, remediation);
    }

    private static OpenIddictManagementException NotFound(string code, string message)
        => new(StatusCodes.Status404NotFound, code, message);

    private static void TrackOperation(string operation, string outcome)
        => Operations.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));

    private OpenIddictClientIntegrationModel BuildIntegration(OpenIddictApplicationModel application)
    {
        return new OpenIddictClientIntegrationModel
        {
            Authority = _authority,
            ClientId = application.ClientId,
            ClientType = application.ClientType,
            GrantTypes = application.Permissions.GrantTypes,
            Scopes = application.Permissions.Scopes,
            RedirectUris = application.RedirectUris,
            PostLogoutRedirectUris = application.PostLogoutRedirectUris
        };
    }

    private static OpenIddictMetadataOptionModel Option(string value, string displayName) => new() { Value = value, DisplayName = displayName };

    private static OpenIddictGrantTypeMetadataModel Grant(string value, string displayName, params string[] allowedClientTypes)
        => new() { Value = value, DisplayName = displayName, AllowedClientTypes = allowedClientTypes };

    private static List<OpenIddictClientTemplateModel> BuildTemplates()
    {
        return
        [
            new OpenIddictClientTemplateModel
            {
                Key = "spa",
                DisplayName = "Single-page application",
                Description = "Browser application using authorization code with PKCE and no client secret.",
                ClientType = ClientTypes.Public,
                RequiresPkce = true,
                Permissions = PermissionModel([GrantTypes.AuthorizationCode, GrantTypes.RefreshToken], ["authorization", "token", "end_session", "revocation"], [Scopes.OpenId, Scopes.Profile, Scopes.Email], [ResponseTypes.Code])
            },
            new OpenIddictClientTemplateModel
            {
                Key = "server-web",
                DisplayName = "Server-rendered web application",
                Description = "Confidential web application using authorization code and a server-held secret.",
                ClientType = ClientTypes.Confidential,
                Permissions = PermissionModel([GrantTypes.AuthorizationCode, GrantTypes.RefreshToken], ["authorization", "token", "end_session", "revocation"], [Scopes.OpenId, Scopes.Profile, Scopes.Email], [ResponseTypes.Code])
            },
            new OpenIddictClientTemplateModel
            {
                Key = "machine",
                DisplayName = "Backend service",
                Description = "Machine-to-machine integration using client credentials and a server-held secret.",
                ClientType = ClientTypes.Confidential,
                Permissions = PermissionModel([GrantTypes.ClientCredentials], ["token", "revocation"], [], [])
            }
        ];
    }

    private static OpenIddictApplicationPermissionModel PermissionModel(
        string[] grantTypes,
        string[] endpoints,
        string[] scopes,
        string[] responseTypes)
    {
        return new OpenIddictApplicationPermissionModel
        {
            GrantTypes = grantTypes,
            Endpoints = endpoints,
            Scopes = scopes,
            ResponseTypes = responseTypes
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class OpenIddictManagementException : Exception
{
    public OpenIddictManagementException(int statusCode, string code, string message, string? remediation = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Remediation = remediation;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public string? Remediation { get; }

    public OpenIddictManagementErrorModel ToModel() => new()
    {
        Code = Code,
        Message = Message,
        Remediation = Remediation
    };
}
