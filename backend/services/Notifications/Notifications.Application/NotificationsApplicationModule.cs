using Notifications.Application.Contracts;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Devices;
using Notifications.Application.Contracts.Emailing;
using Notifications.Application.Contracts.Notifications;
using Notifications.Application.Contracts.Policy;
using Notifications.Application.Contracts.Presence;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.Contracts.Realtime;
using Notifications.Application.Contracts.Templates;
using Notifications.Application.Contracts.UserNotifications;
using Notifications.Application.Contracts.Replay;
using Notifications.Domain;
using Notifications.Application.Digests;
using Notifications.Application.Delivery;
using Notifications.Application.Dispatches;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Application.Delivery.Providers;
using Notifications.Application.Devices;
using Notifications.Application.Emailing;
using Notifications.Application.ProviderConfig;
using Notifications.Application.ProviderConfig.Security;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Application.Templates;
using Notifications.Application.Notifications;
using Notifications.Application.Policy;
using Notifications.Application.Presence;
using Notifications.Application.Providers;
using Notifications.Application.Replay;
using Notifications.Application.Realtime;
using Notifications.Application.UserNotifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.Caching;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Notifications.Application;

[DependsOn(typeof(NotificationsDomainModule))]
[DependsOn(typeof(NotificationsApplicationContractsModule))]
[DependsOn(typeof(CachingModule))]
public class NotificationsApplicationModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddSingleton(TimeProvider.System);
        context.Services.Configure<NotificationsTemplateOptions>(context.Configuration.GetSection("Notifications:Templates"));
        context.Services.Configure<NotificationsEmailOptions>(context.Configuration.GetSection("Notifications:Email"));
        context.Services.AddOptions<NotificationsDeliveryOptions>()
            .Bind(context.Configuration.GetSection("Notifications:Delivery"))
            .Validate(options => options.ProviderTimeoutSeconds is >= 1 and <= 30 &&
                options.ProcessingLeaseSeconds is >= 5 and <= 600 && options.ProcessingLeaseSeconds > options.ProviderTimeoutSeconds,
                "Notification provider deadlines must be between one and thirty seconds and shorter than the bounded processing lease.")
            .ValidateOnStart();
        context.Services.AddOptions<NotificationDispatchOptions>()
            .Bind(context.Configuration.GetSection(NotificationDispatchOptions.SectionName))
            .Validate(options => options.MaximumRecipients is >= 1 and <= 100 &&
                options.MaximumDestinationsPerChannel is >= 1 and <= 10 &&
                options.MaximumTotalDestinations is >= 1 and <= 1000 &&
                options.MaximumPayloadBytes is >= 1024 and <= 262144 &&
                options.DefaultLifetimeDays is >= 1 and <= 30 &&
                options.ReconciliationBatchSize is >= 1 and <= 100,
                "Notification dispatch bounds must stay within the reviewed limits.")
            .ValidateOnStart();
        context.Services.TryAddTransient<INotificationDispatchScheduler, NotificationDispatchScheduler>();
        context.Services.TryAddTransient<INotificationDispatchCoordinator, NotificationDispatchCoordinator>();
        context.Services.TryAddTransient<INotificationDispatchAppService, NotificationDispatchAppService>();
        context.Services.TryAddSingleton<NotificationDispatchHealthState>();
        context.Services.Configure<NotificationsDeliveryLatencyRollupOptions>(
            context.Configuration.GetSection("Notifications:DeliveryLatencyRollup"));
        context.Services.Configure<NotificationsDigestOptions>(context.Configuration.GetSection("Notifications:Digests"));
        context.Services.Configure<NotificationsChannelsOptions>(context.Configuration.GetSection("Notifications:Channels"));
        context.Services.Configure<NotificationsChannelProviderModeOptions>(context.Configuration.GetSection("Notifications:Providers"));
        context.Services.Configure<NotificationsQuerySecurityOptions>(context.Configuration.GetSection("Notifications:Query"));
        context.Services.Configure<NotificationsProviderSecretProtectionOptions>(
            context.Configuration.GetSection(NotificationsProviderSecretProtectionOptions.SectionName));
        context.Services.Configure<NotificationsProviderSecretRotationOptions>(
            context.Configuration.GetSection("Notifications:ProviderSecrets:Rotation"));
        context.Services.AddOptions<IdentityNotificationPreferenceClientOptions>()
            .Bind(context.Configuration.GetSection("IdentityPreferencesClient"))
            .Validate(
                options => !options.Enabled ||
                    (Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _) &&
                     !string.IsNullOrWhiteSpace(options.Scope) &&
                     !string.IsNullOrWhiteSpace(options.ClientProfile) &&
                     options.TimeoutSeconds is >= 1 and <= 30),
                "Identity preference lookups require an absolute base address, dedicated client profile, scope, and bounded timeout.")
            .ValidateOnStart();
        context.Services.AddHttpClient(nameof(InternalAccessTokenProvider));
        context.Services.AddHttpClient(IdentityNotificationPreferenceClient.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityNotificationPreferenceClientOptions>>().Value;
            if (Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 30));
        });
        context.Services.AddOptions<IdentityAudienceClientOptions>()
            .Bind(context.Configuration.GetSection("IdentityAudienceClient"))
            .Validate(
                options => !options.Enabled ||
                    (Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _) &&
                     !string.IsNullOrWhiteSpace(options.Scope) &&
                     !string.IsNullOrWhiteSpace(options.ClientProfile) &&
                     options.TimeoutSeconds is >= 1 and <= 30 &&
                     options.PageSize is >= 1 and <= 500),
                "Identity audience resolution requires an absolute base address, dedicated client profile, scope, bounded timeout, and bounded page size.")
            .ValidateOnStart();
        context.Services.Configure<NotificationAudienceOptions>(context.Configuration.GetSection("Notifications:Audience"));
        context.Services.AddHttpClient(IdentityAudienceClient.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityAudienceClientOptions>>().Value;
            if (Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 30));
        });
        context.Services.TryAddTransient<INotificationDeliveryScheduler, NotificationDeliveryScheduler>();
        context.Services.TryAddTransient<NotificationDigestPlanner>();
        context.Services.TryAddTransient<NotificationDigestProcessor>();
        context.Services.TryAddTransient<IIdentityNotificationPreferenceClient, IdentityNotificationPreferenceClient>();
        context.Services.TryAddTransient<IIdentityAudienceClient, IdentityAudienceClient>();
        context.Services.TryAddTransient<INotificationAudienceResolver, NotificationAudienceResolver>();
        context.Services.TryAddTransient<INotificationDeliveryWorker, NotificationDeliveryWorker>();
        context.Services.TryAddTransient<INotificationDeliveryLatencyRollupProcessor, NotificationDeliveryLatencyRollupProcessor>();
        context.Services.TryAddTransient<IEmailProviderConfigAppService, EmailProviderConfigAppService>();
        context.Services.TryAddTransient<NotificationProviderConfigStore>();
        context.Services.TryAddSingleton<INotificationsProviderSecretProtector, NotificationsProviderSecretProtector>();
        context.Services.TryAddTransient<INotificationProviderConfigResolver, NotificationProviderConfigResolver>();
        context.Services.TryAddTransient<INotificationDeliveryOperationsAppService, NotificationDeliveryOperationsAppService>();
        context.Services.TryAddTransient<INotificationQueryAppService, NotificationQueryAppService>();
        context.Services.TryAddTransient<INotificationPolicyAppService, NotificationPolicyAppService>();
        context.Services.TryAddTransient<INotificationPresenceQueryAppService, NotificationPresenceQueryAppService>();
        context.Services.TryAddTransient<INotificationProviderDiagnosticsAppService, NotificationProviderDiagnosticsAppService>();
        context.Services.TryAddTransient<INotificationReplayAppService, NotificationReplayAppService>();
        context.Services.TryAddTransient<INotificationTemplateAdminAppService, NotificationTemplateAdminAppService>();
        context.Services.TryAddTransient<INotificationsRealtimePublisher, NoopNotificationsRealtimePublisher>();
        context.Services.TryAddTransient<IFileSystemTemplateSource, FileSystemTemplateStore>();
        context.Services.TryAddTransient<ITemplateStore, DatabaseBackedTemplateStore>();
        context.Services.TryAddTransient<NotificationTemplateImportService>();
        context.Services.TryAddTransient<ITemplateRendererAppService, TemplateRendererAppService>();
        context.Services.TryAddTransient<IUserNotificationAppService, UserNotificationAppService>();
        context.Services.TryAddTransient<ISmsNotificationSender, ConfigurableSmsNotificationSender>();
        context.Services.TryAddTransient<ISmsProviderConfigAppService, SmsProviderConfigAppService>();
        context.Services.TryAddTransient<IPushNotificationSender, ConfigurablePushNotificationSender>();
        context.Services.TryAddTransient<IWhatsAppNotificationSender, ConfigurableWhatsAppNotificationSender>();
        context.Services.TryAddTransient<IWhatsAppProviderConfigAppService, WhatsAppProviderConfigAppService>();
        context.Services.TryAddTransient<IPushProviderConfigAppService, PushProviderConfigAppService>();
        context.Services.TryAddTransient<IDeviceRegistrationAppService, DeviceRegistrationAppService>();
        context.Services.TryAddTransient<FcmAccessTokenProvider>();
        context.Services.TryAddTransient<NotificationsProviderSecretRewrapService>();
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IPushProviderAdapter, FcmPushProviderAdapter>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IPushProviderAdapter, ApnsPushProviderAdapter>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IPushProviderAdapter, WebPushProviderAdapter>());
        context.Services.TryAddTransient<ISmtpTransportFactory, SmtpTransportFactory>();
        context.Services.TryAddTransient<SmtpMessageDispatcher>();
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IChannelDeliveryHandler, EmailChannelDeliveryHandler>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IChannelDeliveryHandler, AppChannelDeliveryHandler>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IChannelDeliveryHandler, SmsChannelDeliveryHandler>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IChannelDeliveryHandler, PushChannelDeliveryHandler>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IChannelDeliveryHandler, WhatsAppChannelDeliveryHandler>());
    }
}
