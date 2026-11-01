namespace Notifications.Application.Contracts.Channels;

public class PushProviderConfigDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "Fcm";
    public string ProjectId { get; set; } = "";
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }
    public string? BundleId { get; set; }
    public bool UseSandbox { get; set; }
    public string? VapidSubject { get; set; }
    public string? VapidPublicKey { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public string? SecretFingerprint { get; set; }
    public string? ComplianceContact { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public string? LastTestResult { get; set; }
}

public class CreatePushProviderConfigDto
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "Fcm";
    public string ProjectId { get; set; } = "";
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }
    public string? BundleId { get; set; }
    public bool UseSandbox { get; set; }
    public string? VapidSubject { get; set; }
    public string? VapidPublicKey { get; set; }
    /// <summary>Write-only. Never returned by any read/list endpoint.</summary>
    public string? ClientEmail { get; set; }
    /// <summary>Write-only. Never returned by any read/list endpoint.</summary>
    public string? PrivateKey { get; set; }
    /// <summary>Write-only VAPID private key for Web Push. Never returned by a read endpoint.</summary>
    public string? VapidPrivateKey { get; set; }
    public string? ComplianceContact { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class UpdatePushProviderConfigDto
{
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "Fcm";
    public string ProjectId { get; set; } = "";
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }
    public string? BundleId { get; set; }
    public bool UseSandbox { get; set; }
    public string? VapidSubject { get; set; }
    public string? VapidPublicKey { get; set; }
    /// <summary>Write-only. Omit both secret fields to keep the existing secret unchanged.</summary>
    public string? ClientEmail { get; set; }
    public string? PrivateKey { get; set; }
    public string? VapidPrivateKey { get; set; }
    public string? ComplianceContact { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PushProviderConfigTestSendInput
{
    public string RecipientDeviceToken { get; set; } = "";
}

public class PushProviderConfigTestSendResultDto
{
    public bool Accepted { get; set; }
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string? ProviderMessageId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public interface IPushProviderConfigAppService
{
    Task<List<PushProviderConfigDto>> GetListAsync(Guid? tenantId = null);
    Task<List<PushProviderConfigDto>> GetCurrentTenantListAsync();
    Task<PushProviderConfigDto> GetAsync(Guid id);
    Task<PushProviderConfigDto> CreateAsync(CreatePushProviderConfigDto input);
    Task<PushProviderConfigDto> CreateForCurrentTenantAsync(CreatePushProviderConfigDto input);
    Task<PushProviderConfigDto> UpdateAsync(Guid id, UpdatePushProviderConfigDto input);
    Task DeleteAsync(Guid id);
    Task<PushProviderConfigDto> SetDefaultAsync(Guid id);
    Task<PushProviderConfigTestSendResultDto> SendTestAsync(Guid id, PushProviderConfigTestSendInput input);
}
