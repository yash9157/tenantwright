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
internal sealed class WhatsAppProviderConfigAppService : IWhatsAppProviderConfigAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NotificationProviderConfigStore _store;
    private readonly IEnumerable<IWhatsAppProviderAdapter> _adapters;

    public WhatsAppProviderConfigAppService(NotificationProviderConfigStore store, IEnumerable<IWhatsAppProviderAdapter> adapters)
    {
        _store = store;
        _adapters = adapters;
    }

    public async Task<List<WhatsAppProviderConfigDto>> GetListAsync(Guid? tenantId = null)
    {
        var entities = await _store.GetListAsync(NotificationChannel.WhatsApp, tenantId);
        return entities.Select(Map).ToList();
    }

    public async Task<List<WhatsAppProviderConfigDto>> GetCurrentTenantListAsync()
    {
        var entities = await _store.GetCurrentTenantListAsync(NotificationChannel.WhatsApp);
        return entities.Select(Map).ToList();
    }

    public async Task<WhatsAppProviderConfigDto> GetAsync(Guid id)
    {
        var entity = await _store.GetAsync(NotificationChannel.WhatsApp, id);
        return Map(entity);
    }

    [Audited]
    public async Task<WhatsAppProviderConfigDto> CreateAsync(CreateWhatsAppProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input.PhoneNumberId, input.BusinessAccountId, input.ApprovedTemplateNames, input.TemplateLanguageCode, input.WebhookVerifyToken);
        var secretPlaintext = SerializeSecrets(input.AccessToken, input.AppSecret);
        var entity = await _store.CreateAsync(
            channel: NotificationChannel.WhatsApp,
            tenantId: input.TenantId,
            providerType: input.ProviderType,
            name: input.Name,
            isActive: input.IsActive,
            isDefault: input.IsDefault,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: input.ComplianceContact,
            secretPlaintext: secretPlaintext);

        return Map(entity);
    }

    public Task<WhatsAppProviderConfigDto> CreateForCurrentTenantAsync(CreateWhatsAppProviderConfigDto input)
    {
        input.TenantId = _store.RequireCurrentTenantId();
        return CreateAsync(input);
    }

    [Audited]
    public async Task<WhatsAppProviderConfigDto> UpdateAsync(Guid id, UpdateWhatsAppProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input.PhoneNumberId, input.BusinessAccountId, input.ApprovedTemplateNames, input.TemplateLanguageCode, input.WebhookVerifyToken);
        var secretPlaintext = SerializeSecrets(input.AccessToken, input.AppSecret);
        var entity = await _store.UpdateAsync(
            channel: NotificationChannel.WhatsApp,
            id: id,
            providerType: input.ProviderType,
            name: input.Name,
            isActive: input.IsActive,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: input.ComplianceContact,
            secretPlaintext: secretPlaintext);

        return Map(entity);
    }

    [Audited]
    public Task DeleteAsync(Guid id)
    {
        return _store.DeleteAsync(NotificationChannel.WhatsApp, id);
    }

    [Audited]
    public async Task<WhatsAppProviderConfigDto> SetDefaultAsync(Guid id)
    {
        var entity = await _store.SetDefaultAsync(NotificationChannel.WhatsApp, id);
        return Map(entity);
    }

    [Audited]
    public async Task<WhatsAppProviderConfigTestSendResultDto> SendTestAsync(Guid id, WhatsAppProviderConfigTestSendInput input)
    {
        var recipient = input.RecipientPhoneNumber?.Trim() ?? string.Empty;
        var templateName = input.TemplateName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(templateName))
        {
            throw new ArgumentException("Recipient phone number and template name are required.", nameof(input));
        }

        var entity = await _store.GetAsync(NotificationChannel.WhatsApp, id);
        if (!entity.IsActive)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.ProviderConfigInactive,
                "The selected WhatsApp provider configuration is inactive.");
        }

        var adapter = _adapters.FirstOrDefault(
            x => string.Equals(x.ProviderName, entity.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No WhatsApp provider adapter is registered for provider '{entity.ProviderType}'.");

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
            var result = await adapter.SendAsync(
                recipient,
                templateName,
                "Notifications provider test WhatsApp message. No operator-authored content was used for this test.",
                resolved);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.ErrorCode ?? "whatsapp_provider_failed");
            }

            testResult = "Success";
            return new WhatsAppProviderConfigTestSendResultDto
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

    private static string SerializeSettings(string phoneNumberId, string? businessAccountId, List<string>? approvedTemplateNames, string templateLanguageCode, string? webhookVerifyToken)
    {
        var settings = new MetaWhatsAppCloudSettings
        {
            PhoneNumberId = global::Shared.Check.NotNullOrWhiteSpace(phoneNumberId, nameof(phoneNumberId), maxLength: 64),
            BusinessAccountId = string.IsNullOrWhiteSpace(businessAccountId) ? null : businessAccountId.Trim(),
            ApprovedTemplateNames = (approvedTemplateNames ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TemplateLanguageCode = string.IsNullOrWhiteSpace(templateLanguageCode) ? "en_US" : templateLanguageCode.Trim(),
            WebhookVerifyToken = string.IsNullOrWhiteSpace(webhookVerifyToken) ? null : webhookVerifyToken.Trim()
        };

        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    private static string? SerializeSecrets(string? accessToken, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(appSecret))
        {
            return null;
        }

        return JsonSerializer.Serialize(new MetaWhatsAppCloudSecrets { AccessToken = accessToken, AppSecret = appSecret }, JsonOptions);
    }

    private static WhatsAppProviderConfigDto Map(NotificationProviderConfig entity)
    {
        var settings = JsonSerializer.Deserialize<MetaWhatsAppCloudSettings>(entity.SettingsJson, JsonOptions) ?? new MetaWhatsAppCloudSettings();
        return new WhatsAppProviderConfigDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            Name = entity.Name,
            ProviderType = entity.ProviderType,
            PhoneNumberId = settings.PhoneNumberId,
            BusinessAccountId = settings.BusinessAccountId,
            ApprovedTemplateNames = settings.ApprovedTemplateNames,
            TemplateLanguageCode = settings.TemplateLanguageCode,
            WebhookVerifyToken = settings.WebhookVerifyToken,
            IsActive = entity.IsActive,
            IsDefault = entity.IsDefault,
            SecretFingerprint = entity.SecretFingerprint,
            ComplianceContact = entity.ComplianceContact,
            LastTestedAtUtc = entity.LastTestedAtUtc,
            LastTestResult = entity.LastTestResult
        };
    }
}
