namespace Shared.Data.ConnectionStrings;

public sealed class TenantConnectionStringProtectionOptions
{
    public const string SectionName = "TenantConnectionStrings:Protection";

    public string? CurrentKeyId { get; set; }

    public int CurrentKeyVersion { get; set; } = 1;

    public bool RequireProtectedValues { get; set; }

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
