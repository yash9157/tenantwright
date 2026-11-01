namespace Permissions.Application.PermissionManagement;

public sealed class IdentityPermissionTargetValidationOptions
{
    public string? BaseAddress { get; set; }
    public string Scope { get; set; } = "identity-internal";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 3;
}
