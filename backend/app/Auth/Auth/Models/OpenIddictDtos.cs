using System.ComponentModel.DataAnnotations;

namespace Auth.Models;

public sealed class OpenIddictApplicationCreateInput
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string ClientId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [Required]
    [StringLength(50)]
    public string ClientType { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string ConsentType { get; set; } = string.Empty;

    [Required]
    public OpenIddictApplicationPermissionInput? Permissions { get; set; }

    [Required]
    public List<string>? RedirectUris { get; set; }

    [Required]
    public List<string>? PostLogoutRedirectUris { get; set; }
}

public sealed class OpenIddictApplicationUpdateInput
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string ClientId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [Required]
    [StringLength(50)]
    public string ClientType { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string ConsentType { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ConcurrencyToken { get; set; } = string.Empty;

    [Required]
    public OpenIddictApplicationPermissionInput? Permissions { get; set; }

    [Required]
    public List<string>? RedirectUris { get; set; }

    [Required]
    public List<string>? PostLogoutRedirectUris { get; set; }
}

public sealed class OpenIddictApplicationPermissionInput
{
    [Required]
    public List<string>? GrantTypes { get; set; }

    [Required]
    public List<string>? Endpoints { get; set; }

    [Required]
    public List<string>? Scopes { get; set; }

    [Required]
    public List<string>? ResponseTypes { get; set; }

    [Required]
    public List<string>? AdditionalPermissions { get; set; }
}

public sealed class RotateOpenIddictApplicationSecretInput
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ConcurrencyToken { get; set; } = string.Empty;
}

public sealed class RevokeOpenIddictApplicationSecretInput
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ConcurrencyToken { get; set; } = string.Empty;
}

public sealed class OpenIddictScopeCreateInput
{
    [Required]
    [StringLength(200, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [Required]
    public List<string>? Resources { get; set; }
}

public sealed class OpenIddictScopeUpdateInput
{
    [Required]
    [StringLength(200, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ConcurrencyToken { get; set; } = string.Empty;

    [Required]
    public List<string>? Resources { get; set; }
}

public sealed class RevokeOpenIddictSubjectGrantsInput
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string SubjectConfirmation { get; set; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string ReasonCode { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Note { get; set; }
}
