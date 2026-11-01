using Shared.Caching;
using Identity.Domain.Shared.Account;

namespace Identity.Application.Account;

[IgnoreCacheMultiTenancy]
public sealed class InteractiveLoginChallengeCacheItem
{
    public Guid ChallengeId { get; set; }
    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;
    public Guid? TenantId { get; set; }
    public InteractiveLoginStep ExpectedStep { get; set; } = InteractiveLoginStep.Failed;
    public string LoginInput { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? BrowserInfo { get; set; }
    public string? CorrelationId { get; set; }
    public string? Device { get; set; }
    public string? Location { get; set; }
    public string? DeviceFingerprint { get; set; }
    public bool PromptForTrustDevice { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
