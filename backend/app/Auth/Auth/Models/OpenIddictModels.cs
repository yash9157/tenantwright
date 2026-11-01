namespace Auth.Models;

public sealed class OpenIddictApplicationModel
{
    public string Id { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string ClientType { get; set; } = string.Empty;
    public string ConsentType { get; set; } = string.Empty;
    public string ConcurrencyToken { get; set; } = string.Empty;
    public OpenIddictApplicationPermissionModel Permissions { get; set; } = new();
    public string[] RedirectUris { get; set; } = [];
    public string[] PostLogoutRedirectUris { get; set; } = [];
    public string[] Requirements { get; set; } = [];
    public bool RequiresPkce { get; set; }
    public OpenIddictCredentialMetadataModel Credential { get; set; } = new();
    public OpenIddictManagementOwnershipModel Management { get; set; } = new();
    public DateTimeOffset? LastTokenIssuedAtUtc { get; set; }
}

public sealed class OpenIddictApplicationPermissionModel
{
    public string[] GrantTypes { get; set; } = [];
    public string[] Endpoints { get; set; } = [];
    public string[] Scopes { get; set; } = [];
    public string[] ResponseTypes { get; set; } = [];
    public string[] AdditionalPermissions { get; set; } = [];
}

public sealed class OpenIddictCredentialMetadataModel
{
    public bool IsConfigured { get; set; }
    public DateTimeOffset? LastRotatedAtUtc { get; set; }
    public string? LastRotatedByActor { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedByActor { get; set; }
}

public sealed class OpenIddictManagementOwnershipModel
{
    public string Source { get; set; } = OpenIddictManagementSources.Console;
    public bool IsMutable { get; set; } = true;
    public string? BlockedReason { get; set; }
}

public static class OpenIddictManagementSources
{
    public const string Console = "Console";
    public const string Configuration = "Configuration";
}

public sealed class OpenIddictScopeModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string ConcurrencyToken { get; set; } = string.Empty;
    public string[] Resources { get; set; } = [];
    public OpenIddictManagementOwnershipModel Management { get; set; } = new();
    public string[] ReferencingClientIds { get; set; } = [];
}

public sealed class OpenIddictManagementPage<T>
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; }
    public long TotalCount { get; set; }
    public bool HasMore { get; set; }
    public List<T> Items { get; set; } = [];
}

public sealed class OpenIddictApplicationSecretResult
{
    public string? ClientSecret { get; set; }
    public OpenIddictApplicationModel Application { get; set; } = new();
    public OpenIddictClientIntegrationModel Integration { get; set; } = new();
}

public sealed class OpenIddictClientIntegrationModel
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientType { get; set; } = string.Empty;
    public string[] GrantTypes { get; set; } = [];
    public string[] Scopes { get; set; } = [];
    public string[] RedirectUris { get; set; } = [];
    public string[] PostLogoutRedirectUris { get; set; } = [];
}

public sealed class OpenIddictApplicationImpactModel
{
    public string ClientId { get; set; } = string.Empty;
    public long ActiveAuthorizationCount { get; set; }
    public long ActiveTokenCount { get; set; }
    public Dictionary<string, long> ActiveTokenCountByScope { get; set; } = new(StringComparer.Ordinal);
    public string[] ReferencedScopes { get; set; } = [];
    public bool IsCurrentRequestClient { get; set; }
}

public sealed class OpenIddictManagementMetadataModel
{
    public string Authority { get; set; } = string.Empty;
    public List<OpenIddictMetadataOptionModel> ClientTypes { get; set; } = [];
    public List<OpenIddictMetadataOptionModel> ConsentTypes { get; set; } = [];
    public List<OpenIddictGrantTypeMetadataModel> GrantTypes { get; set; } = [];
    public List<OpenIddictMetadataOptionModel> Endpoints { get; set; } = [];
    public List<OpenIddictMetadataOptionModel> ResponseTypes { get; set; } = [];
    public List<OpenIddictMetadataOptionModel> Scopes { get; set; } = [];
    public List<OpenIddictClientTemplateModel> Templates { get; set; } = [];
}

public class OpenIddictMetadataOptionModel
{
    public string Value { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class OpenIddictGrantTypeMetadataModel : OpenIddictMetadataOptionModel
{
    public string[] AllowedClientTypes { get; set; } = [];
}

public sealed class OpenIddictClientTemplateModel
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ClientType { get; set; } = string.Empty;
    public bool RequiresPkce { get; set; }
    public OpenIddictApplicationPermissionModel Permissions { get; set; } = new();
}

public sealed class OpenIddictManagementErrorModel
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Remediation { get; set; }
}

public sealed class OpenIddictAuthorizationModel
{
    public string Id { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public string? Subject { get; set; }
    public string? Status { get; set; }
    public string? Type { get; set; }
    public string[] Scopes { get; set; } = [];
}

public sealed class OpenIddictTokenModel
{
    public string Id { get; set; } = string.Empty;
    public string? AuthorizationId { get; set; }
    public string? ClientId { get; set; }
    public string? Subject { get; set; }
    public string? Status { get; set; }
    public string? Type { get; set; }
    public DateTimeOffset? CreationDate { get; set; }
    public DateTimeOffset? ExpirationDate { get; set; }
    public DateTimeOffset? RedemptionDate { get; set; }
}

public sealed class OpenIddictRevocationResult
{
    public string Id { get; set; } = string.Empty;
    public string? PreviousStatus { get; set; }
    public string? CurrentStatus { get; set; }
    public bool WasAlreadyRevoked { get; set; }
}

public sealed class OpenIddictSubjectGrantRevocationResult
{
    public string Subject { get; set; } = string.Empty;
    public long RevokedAuthorizationCount { get; set; }
    public long RevokedTokenCount { get; set; }
    public bool WasAlreadyRevoked { get; set; }
}
