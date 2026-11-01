namespace Notifications.Application.Contracts.Emailing;

public class EmailProviderConfigDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? UserName { get; set; }

    /// <summary>
    /// True when an SMTP password is configured for this provider. The password itself is
    /// write-only (accepted on create/update, encrypted at rest) and is never returned.
    /// </summary>
    public bool HasPasswordSecret { get; set; }
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string SecureSocketOption { get; set; } = "StartTls";
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public string? LastTestResult { get; set; }
}

public class CreateEmailProviderConfigDto
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? PasswordSecretKey { get; set; }
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string SecureSocketOption { get; set; } = "StartTls";
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class UpdateEmailProviderConfigDto
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? PasswordSecretKey { get; set; }
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string SecureSocketOption { get; set; } = "StartTls";
    public bool IsActive { get; set; } = true;
}

public class EmailProviderConfigTestSendInput
{
    public string RecipientEmail { get; set; } = "";
}

public class EmailProviderConfigTestSendResultDto
{
    public bool Accepted { get; set; }
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string? ProviderMessageId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public interface IEmailProviderConfigAppService
{
    Task<List<EmailProviderConfigDto>> GetListAsync(Guid? tenantId = null);
    Task<List<EmailProviderConfigDto>> GetCurrentTenantListAsync();
    Task<EmailProviderConfigDto> GetAsync(Guid id);
    Task<EmailProviderConfigDto> CreateAsync(CreateEmailProviderConfigDto input);
    Task<EmailProviderConfigDto> CreateForCurrentTenantAsync(CreateEmailProviderConfigDto input);
    Task<EmailProviderConfigDto> UpdateAsync(Guid id, UpdateEmailProviderConfigDto input);
    Task DeleteAsync(Guid id);
    Task<EmailProviderConfigDto> SetDefaultAsync(Guid id);
    Task<EmailProviderConfigTestSendResultDto> SendTestAsync(Guid id, EmailProviderConfigTestSendInput input);
}
