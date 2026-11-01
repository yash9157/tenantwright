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
internal sealed class PushProviderConfigAppService : IPushProviderConfigAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NotificationProviderConfigStore _store;
    private readonly IEnumerable<IPushProviderAdapter> _adapters;

    public PushProviderConfigAppService(NotificationProviderConfigStore store, IEnumerable<IPushProviderAdapter> adapters)
    {
        _store = store;
        _adapters = adapters;
    }

    public async Task<List<PushProviderConfigDto>> GetListAsync(Guid? tenantId = null)
    {
        var entities = await _store.GetListAsync(NotificationChannel.Push, tenantId);
        return entities.Select(Map).ToList();
    }

    public async Task<List<PushProviderConfigDto>> GetCurrentTenantListAsync()
    {
        var entities = await _store.GetCurrentTenantListAsync(NotificationChannel.Push);
        return entities.Select(Map).ToList();
    }

    public async Task<PushProviderConfigDto> GetAsync(Guid id)
    {
        var entity = await _store.GetAsync(NotificationChannel.Push, id);
        return Map(entity);
    }

    [Audited]
    public async Task<PushProviderConfigDto> CreateAsync(CreatePushProviderConfigDto input)
    {
        var settingsJson = SerializeSettings(input);
        var secretPlaintext = SerializeSecrets(input);
        var entity = await _store.CreateAsync(
            channel: NotificationChannel.Push,
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

    public Task<PushProviderConfigDto> CreateForCurrentTenantAsync(CreatePushProviderConfigDto input)
    {
        input.TenantId = _store.RequireCurrentTenantId();
        return CreateAsync(input);
    }

    [Audited]
    public async Task<PushProviderConfigDto> UpdateAsync(Guid id, UpdatePushProviderConfigDto input)
    {
        var existing = await _store.GetAsync(NotificationChannel.Push, id);
        if (!string.Equals(existing.ProviderType, input.ProviderType, StringComparison.Ordinal))
        {
            throw new SharedConflictException(
                NotificationErrorCodes.PushProviderTypeImmutable,
                "Push provider type is immutable. Create a new configuration to change provider.");
        }
        if (string.Equals(existing.ProviderType, "Apns", StringComparison.Ordinal))
        {
            var prior = JsonSerializer.Deserialize<ApnsSettings>(existing.SettingsJson, JsonOptions) ?? new();
            if (prior.UseSandbox != input.UseSandbox)
            {
                throw new SharedConflictException(
                    NotificationErrorCodes.ApnsEnvironmentImmutable,
                    "APNs sandbox/production selection is immutable. Create a new configuration to change environment.");
            }
        }
        var settingsJson = SerializeSettings(input);
        var secretPlaintext = SerializeSecrets(input);
        var entity = await _store.UpdateAsync(
            channel: NotificationChannel.Push,
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
        return _store.DeleteAsync(NotificationChannel.Push, id);
    }

    [Audited]
    public async Task<PushProviderConfigDto> SetDefaultAsync(Guid id)
    {
        var entity = await _store.SetDefaultAsync(NotificationChannel.Push, id);
        return Map(entity);
    }

    [Audited]
    public async Task<PushProviderConfigTestSendResultDto> SendTestAsync(Guid id, PushProviderConfigTestSendInput input)
    {
        var recipient = input.RecipientDeviceToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new ArgumentException("Recipient device token is required.", nameof(input));
        }

        var entity = await _store.GetAsync(NotificationChannel.Push, id);
        if (!entity.IsActive)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.ProviderConfigInactive,
                "The selected push provider configuration is inactive.");
        }

        var adapter = _adapters.FirstOrDefault(
            x => string.Equals(x.ProviderName, entity.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No push provider adapter is registered for provider '{entity.ProviderType}'.");

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
                "Notifications provider test",
                "No operator-authored content was used for this test.",
                resolved);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.ErrorCode ?? "push_provider_failed");
            }

            testResult = "Success";
            return new PushProviderConfigTestSendResultDto
            {
                Accepted = true,
                Id = entity.Id,
                TenantId = entity.TenantId,
                ProviderMessageId = result.ProviderMessageId,
                SentAtUtc = sentAtUtc
            };
        }
        finally
        {
            await _store.RecordTestResultAsync(entity, sentAtUtc, testResult);
        }
    }

    private static string SerializeSettings(CreatePushProviderConfigDto input)
    {
        return input.ProviderType.Trim() switch
        {
            "Fcm" => JsonSerializer.Serialize(new FcmSettings
            {
                ProjectId = global::Shared.Check.NotNullOrWhiteSpace(input.ProjectId, nameof(input.ProjectId), maxLength: 128)
            }, JsonOptions),
            "Apns" => JsonSerializer.Serialize(new ApnsSettings
            {
                TeamId = global::Shared.Check.NotNullOrWhiteSpace(input.TeamId, nameof(input.TeamId), maxLength: 32),
                KeyId = global::Shared.Check.NotNullOrWhiteSpace(input.KeyId, nameof(input.KeyId), maxLength: 32),
                BundleId = global::Shared.Check.NotNullOrWhiteSpace(input.BundleId, nameof(input.BundleId), maxLength: 256),
                UseSandbox = input.UseSandbox
            }, JsonOptions),
            "WebPush" => JsonSerializer.Serialize(new WebPushSettings
            {
                Subject = global::Shared.Check.NotNullOrWhiteSpace(input.VapidSubject, nameof(input.VapidSubject), maxLength: 256),
                PublicKey = global::Shared.Check.NotNullOrWhiteSpace(input.VapidPublicKey, nameof(input.VapidPublicKey), maxLength: 256)
            }, JsonOptions),
            _ => throw new ArgumentException("Push provider type must be Fcm, Apns, or WebPush.", nameof(input))
        };
    }

    private static string SerializeSettings(UpdatePushProviderConfigDto input)
    {
        return SerializeSettings(new CreatePushProviderConfigDto
        {
            ProviderType = input.ProviderType,
            ProjectId = input.ProjectId,
            TeamId = input.TeamId,
            KeyId = input.KeyId,
            BundleId = input.BundleId,
            UseSandbox = input.UseSandbox,
            VapidSubject = input.VapidSubject,
            VapidPublicKey = input.VapidPublicKey
        });
    }

    private static string? SerializeSecrets(CreatePushProviderConfigDto input)
        => SerializeSecrets(input.ProviderType, input.ClientEmail, input.PrivateKey, input.VapidPrivateKey);

    private static string? SerializeSecrets(UpdatePushProviderConfigDto input)
        => SerializeSecrets(input.ProviderType, input.ClientEmail, input.PrivateKey, input.VapidPrivateKey);

    private static string? SerializeSecrets(string providerType, string? clientEmail, string? privateKey, string? vapidPrivateKey)
    {
        return providerType.Trim() switch
        {
            "Fcm" when string.IsNullOrWhiteSpace(clientEmail) && string.IsNullOrWhiteSpace(privateKey) => null,
            "Fcm" => JsonSerializer.Serialize(new FcmSecrets { ClientEmail = clientEmail, PrivateKey = privateKey }, JsonOptions),
            "Apns" when string.IsNullOrWhiteSpace(privateKey) => null,
            "Apns" => JsonSerializer.Serialize(new ApnsSecrets { PrivateKey = privateKey }, JsonOptions),
            "WebPush" when string.IsNullOrWhiteSpace(vapidPrivateKey) => null,
            "WebPush" => JsonSerializer.Serialize(new WebPushSecrets { PrivateKey = vapidPrivateKey }, JsonOptions),
            _ => throw new ArgumentException("Push provider type must be Fcm, Apns, or WebPush.", nameof(providerType))
        };
    }

    private static PushProviderConfigDto Map(NotificationProviderConfig entity)
    {
        var dto = new PushProviderConfigDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            Name = entity.Name,
            ProviderType = entity.ProviderType,
            IsActive = entity.IsActive,
            IsDefault = entity.IsDefault,
            SecretFingerprint = entity.SecretFingerprint,
            ComplianceContact = entity.ComplianceContact,
            LastTestedAtUtc = entity.LastTestedAtUtc,
            LastTestResult = entity.LastTestResult
        };
        switch (entity.ProviderType)
        {
            case "Fcm": dto.ProjectId = (JsonSerializer.Deserialize<FcmSettings>(entity.SettingsJson, JsonOptions) ?? new()).ProjectId; break;
            case "Apns":
                var apns = JsonSerializer.Deserialize<ApnsSettings>(entity.SettingsJson, JsonOptions) ?? new();
                dto.TeamId = apns.TeamId; dto.KeyId = apns.KeyId; dto.BundleId = apns.BundleId; dto.UseSandbox = apns.UseSandbox; break;
            case "WebPush":
                var webPush = JsonSerializer.Deserialize<WebPushSettings>(entity.SettingsJson, JsonOptions) ?? new();
                dto.VapidSubject = webPush.Subject; dto.VapidPublicKey = webPush.PublicKey; break;
        }
        return dto;
    }
}
