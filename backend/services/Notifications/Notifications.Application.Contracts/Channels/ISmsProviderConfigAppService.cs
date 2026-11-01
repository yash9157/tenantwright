namespace Notifications.Application.Contracts.Channels;

public class SmsProviderConfigDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "Twilio";
    public string AccountSid { get; set; } = "";
    public string FromNumber { get; set; } = "";
    public string? MessagingServiceSid { get; set; }
    public string? WebhookBaseUrl { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public string? SecretFingerprint { get; set; }
    public string? ComplianceContact { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public string? LastTestResult { get; set; }
}

public class CreateSmsProviderConfigDto
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "Twilio";
    public string AccountSid { get; set; } = "";
    public string FromNumber { get; set; } = "";
    public string? MessagingServiceSid { get; set; }
    public string? WebhookBaseUrl { get; set; }
    /// <summary>Write-only. Never returned by any read/list endpoint.</summary>
    public string? AuthToken { get; set; }
    public string? ComplianceContact { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class UpdateSmsProviderConfigDto
{
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "Twilio";
    public string AccountSid { get; set; } = "";
    public string FromNumber { get; set; } = "";
    public string? MessagingServiceSid { get; set; }
    public string? WebhookBaseUrl { get; set; }
    /// <summary>Write-only. Omit/null to keep the existing secret unchanged.</summary>
    public string? AuthToken { get; set; }
    public string? ComplianceContact { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SmsProviderConfigTestSendInput
{
    public string RecipientPhoneNumber { get; set; } = "";
}

public class SmsProviderConfigTestSendResultDto
{
    public bool Accepted { get; set; }
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string RecipientPhoneNumber { get; set; } = "";
    public string? ProviderMessageId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public interface ISmsProviderConfigAppService
{
    Task<List<SmsProviderConfigDto>> GetListAsync(Guid? tenantId = null);
    Task<List<SmsProviderConfigDto>> GetCurrentTenantListAsync();
    Task<SmsProviderConfigDto> GetAsync(Guid id);
    Task<SmsProviderConfigDto> CreateAsync(CreateSmsProviderConfigDto input);
    Task<SmsProviderConfigDto> CreateForCurrentTenantAsync(CreateSmsProviderConfigDto input);
    Task<SmsProviderConfigDto> UpdateAsync(Guid id, UpdateSmsProviderConfigDto input);
    Task DeleteAsync(Guid id);
    Task<SmsProviderConfigDto> SetDefaultAsync(Guid id);
    Task<SmsProviderConfigTestSendResultDto> SendTestAsync(Guid id, SmsProviderConfigTestSendInput input);
}
