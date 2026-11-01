using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public sealed class IdentityPasskeyCredential : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public string CredentialIdHash { get; set; } = string.Empty;
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public byte[] UserHandle { get; set; } = [];
    public uint SignatureCounter { get; set; }
    public Guid AaGuid { get; set; }
    public string? Transports { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
}
