namespace Notifications.Application.Contracts.Channels;

public class WhatsAppProviderConfigDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "MetaWhatsAppCloud";
    public string PhoneNumberId { get; set; } = "";
    public string? BusinessAccountId { get; set; }
    public List<string> ApprovedTemplateNames { get; set; } = [];
    public string TemplateLanguageCode { get; set; } = "en_US";
    public string? WebhookVerifyToken { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public string? SecretFingerprint { get; set; }
    public string? ComplianceContact { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public string? LastTestResult { get; set; }
}

public class CreateWhatsAppProviderConfigDto
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "MetaWhatsAppCloud";
    public string PhoneNumberId { get; set; } = "";
    public string? BusinessAccountId { get; set; }
    public List<string> ApprovedTemplateNames { get; set; } = [];
    public string TemplateLanguageCode { get; set; } = "en_US";
    public string? WebhookVerifyToken { get; set; }
    /// <summary>Write-only. Never returned by any read/list endpoint.</summary>
    public string? AccessToken { get; set; }
    /// <summary>
    /// Write-only. Only meaningful on the host default config — signs webhook callbacks for the
    /// whole Meta App, not a per-tenant value.
    /// </summary>
    public string? AppSecret { get; set; }
    public string? ComplianceContact { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class UpdateWhatsAppProviderConfigDto
{
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "MetaWhatsAppCloud";
    public string PhoneNumberId { get; set; } = "";
    public string? BusinessAccountId { get; set; }
    public List<string> ApprovedTemplateNames { get; set; } = [];
    public string TemplateLanguageCode { get; set; } = "en_US";
    public string? WebhookVerifyToken { get; set; }
    /// <summary>Write-only. Omit both secret fields to keep the existing secret unchanged.</summary>
    public string? AccessToken { get; set; }
    public string? AppSecret { get; set; }
    public string? ComplianceContact { get; set; }
    public bool IsActive { get; set; } = true;
}

public class WhatsAppProviderConfigTestSendInput
{
    public string RecipientPhoneNumber { get; set; } = "";
    public string TemplateName { get; set; } = "";
}

public class WhatsAppProviderConfigTestSendResultDto
{
    public bool Accepted { get; set; }
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string RecipientPhoneNumber { get; set; } = "";
    public string? ProviderMessageId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public interface IWhatsAppProviderConfigAppService
{
    Task<List<WhatsAppProviderConfigDto>> GetListAsync(Guid? tenantId = null);
    Task<List<WhatsAppProviderConfigDto>> GetCurrentTenantListAsync();
    Task<WhatsAppProviderConfigDto> GetAsync(Guid id);
    Task<WhatsAppProviderConfigDto> CreateAsync(CreateWhatsAppProviderConfigDto input);
    Task<WhatsAppProviderConfigDto> CreateForCurrentTenantAsync(CreateWhatsAppProviderConfigDto input);
    Task<WhatsAppProviderConfigDto> UpdateAsync(Guid id, UpdateWhatsAppProviderConfigDto input);
    Task DeleteAsync(Guid id);
    Task<WhatsAppProviderConfigDto> SetDefaultAsync(Guid id);
    Task<WhatsAppProviderConfigTestSendResultDto> SendTestAsync(Guid id, WhatsAppProviderConfigTestSendInput input);
}
