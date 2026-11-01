using System.Net.Mail;
using System.Text.Json;
using MimeKit;
using MimeKit.Text;
using Notifications.Application.Contracts.Emailing;
using Notifications.Application.ProviderConfig;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Modularity.DependencyInjection;

using Shared.AuditLogs.Auditing;
namespace Notifications.Application.Emailing;

[DisableConventionalRegistration]
internal sealed class EmailProviderConfigAppService : IEmailProviderConfigAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ProviderType = "Smtp";

    private readonly NotificationProviderConfigStore _store;
    private readonly SmtpMessageDispatcher _smtpDispatcher;

    public EmailProviderConfigAppService(
        NotificationProviderConfigStore store,
        SmtpMessageDispatcher smtpDispatcher)
    {
        _store = store;
        _smtpDispatcher = smtpDispatcher;
    }

    public async Task<List<EmailProviderConfigDto>> GetListAsync(Guid? tenantId = null)
    {
        var entities = await _store.GetListAsync(NotificationChannel.Email, tenantId);
        return entities.Select(Map).ToList();
    }

    public async Task<List<EmailProviderConfigDto>> GetCurrentTenantListAsync()
    {
        var entities = await _store.GetCurrentTenantListAsync(NotificationChannel.Email);
        return entities.Select(Map).ToList();
    }

    public async Task<EmailProviderConfigDto> GetAsync(Guid id)
    {
        var entity = await _store.GetAsync(NotificationChannel.Email, id);
        return Map(entity);
    }

    [Audited]
    public async Task<EmailProviderConfigDto> CreateAsync(CreateEmailProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input.Host, input.Port, input.UserName, input.FromAddress, input.FromName, input.SecureSocketOption);
        var entity = await _store.CreateAsync(
            channel: NotificationChannel.Email,
            tenantId: input.TenantId,
            providerType: ProviderType,
            name: input.Name,
            isActive: input.IsActive,
            isDefault: input.IsDefault,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: null,
            secretPlaintext: input.PasswordSecretKey);

        return Map(entity);
    }

    public Task<EmailProviderConfigDto> CreateForCurrentTenantAsync(CreateEmailProviderConfigDto input)
    {
        input.TenantId = _store.RequireCurrentTenantId();
        return CreateAsync(input);
    }

    [Audited]
    public async Task<EmailProviderConfigDto> UpdateAsync(Guid id, UpdateEmailProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input.Host, input.Port, input.UserName, input.FromAddress, input.FromName, input.SecureSocketOption);
        var entity = await _store.UpdateAsync(
            channel: NotificationChannel.Email,
            id: id,
            providerType: ProviderType,
            name: input.Name,
            isActive: input.IsActive,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: null,
            secretPlaintext: input.PasswordSecretKey);

        return Map(entity);
    }

    [Audited]
    public Task DeleteAsync(Guid id)
    {
        return _store.DeleteAsync(NotificationChannel.Email, id);
    }

    [Audited]
    public async Task<EmailProviderConfigDto> SetDefaultAsync(Guid id)
    {
        var entity = await _store.SetDefaultAsync(NotificationChannel.Email, id);
        return Map(entity);
    }

    [Audited]
    public async Task<EmailProviderConfigTestSendResultDto> SendTestAsync(Guid id, EmailProviderConfigTestSendInput input)
    {
        var recipientEmail = input.RecipientEmail?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            throw new ArgumentException("Recipient email is required.", nameof(input));
        }

        _ = new MailAddress(recipientEmail);

        var entity = await _store.GetAsync(NotificationChannel.Email, id);

        if (!entity.IsActive)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.ProviderConfigInactive,
                "The selected email provider configuration is inactive.");
        }

        var settings = DeserializeSettings(entity.SettingsJson);
        var sentAtUtc = DateTime.UtcNow;
        var contextLabel = entity.TenantId.HasValue ? $"tenant {entity.TenantId:D}" : "host default";
        var subject = $"Notifications provider test email ({entity.Name}, {contextLabel})";
        var htmlBody =
            $"""
            <h1>Notifications Provider Test Email</h1>
            <p>This diagnostic email was sent from the Notifications admin configuration page.</p>
            <ul>
              <li><strong>Configuration:</strong> {entity.Name}</li>
              <li><strong>Context:</strong> {contextLabel}</li>
              <li><strong>Sent at (UTC):</strong> {sentAtUtc:O}</li>
              <li><strong>Recipient:</strong> {recipientEmail}</li>
            </ul>
            <p>No operator-authored content was used for this test.</p>
            """;

        var message = new MimeMessage();
        message.From.Add(string.IsNullOrWhiteSpace(settings.FromName)
            ? MailboxAddress.Parse(settings.FromAddress)
            : new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = subject;
        message.Body = new TextPart(TextFormat.Html) { Text = htmlBody };

        string? providerMessageId = null;
        var testResult = "Failed";
        try
        {
            providerMessageId = await _smtpDispatcher.SendAsync(
                settings.Host,
                settings.Port,
                EmailProviderConfigSmtpHelpers.ParseSecureSocketOptions(settings.SecureSocketOption),
                settings.UserName,
                _store.DecryptSecret(entity),
                message);
            testResult = "Success";
        }
        finally
        {
            await _store.RecordTestResultAsync(entity, sentAtUtc, testResult);
        }

        return new EmailProviderConfigTestSendResultDto
        {
            Accepted = true,
            Id = entity.Id,
            TenantId = entity.TenantId,
            RecipientEmail = recipientEmail,
            ProviderMessageId = providerMessageId,
            SentAtUtc = sentAtUtc
        };
    }

    internal static string SerializeSettings(string host, int port, string? userName, string fromAddress, string? fromName, string secureSocketOption)
    {
        var settings = new EmailProviderSettings
        {
            Host = global::Shared.Check.NotNullOrWhiteSpace(host, nameof(host), maxLength: 256),
            Port = port <= 0 ? 587 : port,
            UserName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim(),
            FromAddress = global::Shared.Check.NotNullOrWhiteSpace(fromAddress, nameof(fromAddress), maxLength: 256),
            FromName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim(),
            SecureSocketOption = global::Shared.Check.NotNullOrWhiteSpace(secureSocketOption, nameof(secureSocketOption), maxLength: 32)
        };

        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    internal static EmailProviderSettings DeserializeSettings(string settingsJson)
    {
        return JsonSerializer.Deserialize<EmailProviderSettings>(settingsJson, JsonOptions)
            ?? new EmailProviderSettings();
    }

    private static EmailProviderConfigDto Map(NotificationProviderConfig entity)
    {
        var settings = DeserializeSettings(entity.SettingsJson);
        return new EmailProviderConfigDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            Name = entity.Name,
            Host = settings.Host,
            Port = settings.Port,
            UserName = settings.UserName,
            HasPasswordSecret = !string.IsNullOrWhiteSpace(entity.SecretFingerprint),
            FromAddress = settings.FromAddress,
            FromName = settings.FromName,
            SecureSocketOption = settings.SecureSocketOption,
            IsActive = entity.IsActive,
            IsDefault = entity.IsDefault,
            LastTestedAtUtc = entity.LastTestedAtUtc,
            LastTestResult = entity.LastTestResult
        };
    }
}
