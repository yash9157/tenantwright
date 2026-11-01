using Shared.Caching;

namespace Identity.Application.Passkeys;

public enum PasskeyCeremonyPurpose
{
    SelfServiceRegistration = 1,
    InteractiveEnrollment = 2,
    InteractiveAssertion = 3
}

[IgnoreCacheMultiTenancy]
public sealed class PasskeyCeremonyCacheItem
{
    public Guid CeremonyId { get; set; }
    public PasskeyCeremonyPurpose Purpose { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? InteractiveChallengeId { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string OriginalOptionsJson { get; set; } = string.Empty;
    public string ConfigurationFingerprint { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
