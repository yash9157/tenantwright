using System.Text.Json;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.ProviderConfig;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Modularity.DependencyInjection;

using Shared.AuditLogs.Auditing;
namespace Notifications.Application.Delivery;

[DisableConventionalRegistration]
internal sealed class SmsProviderConfigAppService : ISmsProviderConfigAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NotificationProviderConfigStore _store;
    private readonly IEnumerable<ISmsProviderAdapter> _adapters;

    public SmsProviderConfigAppService(NotificationProviderConfigStore store, IEnumerable<ISmsProviderAdapter> adapters)
    {
        _store = store;
        _adapters = adapters;
    }

    public async Task<List<SmsProviderConfigDto>> GetListAsync(Guid? tenantId = null)
    {
        var entities = await _store.GetListAsync(NotificationChannel.Sms, tenantId);
        return entities.Select(Map).ToList();
    }

    public async Task<List<SmsProviderConfigDto>> GetCurrentTenantListAsync()
    {
        var entities = await _store.GetCurrentTenantListAsync(NotificationChannel.Sms);
        return entities.Select(Map).ToList();
    }

    public async Task<SmsProviderConfigDto> GetAsync(Guid id)
    {
        var entity = await _store.GetAsync(NotificationChannel.Sms, id);
        return Map(entity);
    }

    [Audited]
    public async Task<SmsProviderConfigDto> CreateAsync(CreateSmsProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input.AccountSid, input.FromNumber, input.MessagingServiceSid, input.WebhookBaseUrl);
        var entity = await _store.CreateAsync(
            channel: NotificationChannel.Sms,
            tenantId: input.TenantId,
            providerType: input.ProviderType,
            name: input.Name,
            isActive: input.IsActive,
            isDefault: input.IsDefault,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: input.ComplianceContact,
            secretPlaintext: input.AuthToken);

        return Map(entity);
    }

    public Task<SmsProviderConfigDto> CreateForCurrentTenantAsync(CreateSmsProviderConfigDto input)
    {
        input.TenantId = _store.RequireCurrentTenantId();
        return CreateAsync(input);
    }

    [Audited]
    public async Task<SmsProviderConfigDto> UpdateAsync(Guid id, UpdateSmsProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input.AccountSid, input.FromNumber, input.MessagingServiceSid, input.WebhookBaseUrl);
        var entity = await _store.UpdateAsync(
            channel: NotificationChannel.Sms,
            id: id,
            providerType: input.ProviderType,
            name: input.Name,
            isActive: input.IsActive,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: input.ComplianceContact,
            secretPlaintext: input.AuthToken);

        return Map(entity);
    }

    [Audited]
    public Task DeleteAsync(Guid id)
    {
        return _store.DeleteAsync(NotificationChannel.Sms, id);
    }

    [Audited]
    public async Task<SmsProviderConfigDto> SetDefaultAsync(Guid id)
    {
        var entity = await _store.SetDefaultAsync(NotificationChannel.Sms, id);
        return Map(entity);
    }

    [Audited]
    public async Task<SmsProviderConfigTestSendResultDto> SendTestAsync(Guid id, SmsProviderConfigTestSendInput input)
    {
        var recipient = input.RecipientPhoneNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new ArgumentException("Recipient phone number is required.", nameof(input));
        }

        var entity = await _store.GetAsync(NotificationChannel.Sms, id);
        if (!entity.IsActive)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.ProviderConfigInactive,
                "The selected SMS provider configuration is inactive.");
        }

        var adapter = _adapters.FirstOrDefault(
            x => string.Equals(x.ProviderName, entity.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No SMS provider adapter is registered for provider '{entity.ProviderType}'.");

        var resolved = new ResolvedNotificationProviderConfig
        {
            ConfigId = entity.Id,
            ConfigTenantId = entity.TenantId,
            Channel = entity.Channel,
            ProviderType = entity.ProviderType,
            SettingsJson = entity.SettingsJson,
            Secret = _store.DecryptSecret(entity)
        };

        var sentAtUtc = DateTime.UtcNow;
        var testResult = "Failed";
        try
        {
            var result = await adapter.SendAsync(recipient, "Notifications provider test SMS. No operator-authored content was used for this test.", resolved);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.ErrorCode ?? "sms_provider_failed");
            }

            testResult = "Success";
            return new SmsProviderConfigTestSendResultDto
            {
                Accepted = true,
                Id = entity.Id,
                TenantId = entity.TenantId,
                RecipientPhoneNumber = recipient,
                ProviderMessageId = result.ProviderMessageId,
                SentAtUtc = sentAtUtc
            };
        }
        finally
        {
            await _store.RecordTestResultAsync(entity, sentAtUtc, testResult);
        }
    }

    private static string SerializeSettings(string accountSid, string fromNumber, string? messagingServiceSid, string? webhookBaseUrl)
    {
        var settings = new TwilioSmsSettings
        {
            AccountSid = global::Shared.Check.NotNullOrWhiteSpace(accountSid, nameof(accountSid), maxLength: 128),
            FromNumber = global::Shared.Check.NotNullOrWhiteSpace(fromNumber, nameof(fromNumber), maxLength: 32),
            MessagingServiceSid = string.IsNullOrWhiteSpace(messagingServiceSid) ? null : messagingServiceSid.Trim(),
            WebhookBaseUrl = string.IsNullOrWhiteSpace(webhookBaseUrl) ? null : webhookBaseUrl.Trim()
        };

        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    private static SmsProviderConfigDto Map(NotificationProviderConfig entity)
    {
        var settings = JsonSerializer.Deserialize<TwilioSmsSettings>(entity.SettingsJson, JsonOptions) ?? new TwilioSmsSettings();
        return new SmsProviderConfigDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            Name = entity.Name,
            ProviderType = entity.ProviderType,
            AccountSid = settings.AccountSid,
            FromNumber = settings.FromNumber,
            MessagingServiceSid = settings.MessagingServiceSid,
            WebhookBaseUrl = settings.WebhookBaseUrl,
            IsActive = entity.IsActive,
            IsDefault = entity.IsDefault,
            SecretFingerprint = entity.SecretFingerprint,
            ComplianceContact = entity.ComplianceContact,
            LastTestedAtUtc = entity.LastTestedAtUtc,
            LastTestResult = entity.LastTestResult
        };
    }
}
