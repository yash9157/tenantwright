using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Application.Delivery;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;

namespace Notifications.Data;

/// <summary>
/// One-time bootstrap: if the flat Notifications:Sms:* appsettings/env credentials are configured
/// and no host-default Sms provider configuration exists yet, create one, so an existing
/// single-account deployment keeps working after the cutover to scoped provider configs without
/// manual re-entry. No-ops once a host-default Sms config exists (regardless of who created it).
/// </summary>
public sealed class SmsProviderConfigBootstrapSeedContributor : IDataSeedContributor, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly IOptions<NotificationsSmsProviderOptions> _smsOptions;
    private readonly INotificationsProviderSecretProtector _protector;

    public SmsProviderConfigBootstrapSeedContributor(
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        IOptions<NotificationsSmsProviderOptions> smsOptions,
        INotificationsProviderSecretProtector protector)
    {
        _dbContextProvider = dbContextProvider;
        _smsOptions = smsOptions;
        _protector = protector;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        // Host bootstrap only. Tenant seeding (a dedicated tenant database being provisioned) must never copy the
        // host's provider credentials into a tenant-owned database.
        if (context.TenantId.HasValue)
        {
            return;
        }

        var options = _smsOptions.Value;
        if (string.IsNullOrWhiteSpace(options.AccountSid) ||
            string.IsNullOrWhiteSpace(options.AuthToken) ||
            string.IsNullOrWhiteSpace(options.FromNumber))
        {
            return;
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var hostDefaultExists = await dbContext.NotificationProviderConfigs
            .AnyAsync(x => x.TenantId == null && x.Channel == NotificationChannel.Sms && x.IsDefault);
        if (hostDefaultExists)
        {
            return;
        }

        var settingsJson = JsonSerializer.Serialize(
            new TwilioSmsSettings
            {
                AccountSid = options.AccountSid,
                FromNumber = options.FromNumber,
                WebhookBaseUrl = options.WebhookBaseUrl
            },
            JsonOptions);

        var entity = new NotificationProviderConfig(
            id: Guid.CreateVersion7(),
            tenantId: null,
            channel: NotificationChannel.Sms,
            providerType: options.Provider,
            name: "Bootstrap",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: settingsJson,
            complianceContact: null);

        var envelope = _protector.Protect(null, NotificationChannel.Sms, entity.Id, options.AuthToken);
        entity.SetSecret(envelope, _protector.Fingerprint(options.AuthToken));

        dbContext.NotificationProviderConfigs.Add(entity);
        await dbContext.SaveChangesAsync();
    }
}
