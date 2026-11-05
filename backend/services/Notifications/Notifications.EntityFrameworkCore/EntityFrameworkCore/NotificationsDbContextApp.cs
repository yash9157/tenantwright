using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Archival;
using Notifications.Domain.Digests;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Devices;
using Notifications.Domain.Notifications;
using Notifications.Domain.Policy;
using Notifications.Domain.Presence;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Templates;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;

namespace Notifications.EntityFrameworkCore.EntityFrameworkCore;

[ConnectionStringName(TenantWrightDatabaseCatalog.Notifications)]
public class NotificationsDbContextApp : SharedDbContext<NotificationsDbContextApp>
{

    public DbSet<Notification> Notifications { get; set; } = default!;
    public DbSet<NotificationDispatch> NotificationDispatches => Set<NotificationDispatch>();
    public DbSet<NotificationDispatchRecipient> NotificationDispatchRecipients => Set<NotificationDispatchRecipient>();
    public DbSet<NotificationDispatchStep> NotificationDispatchSteps => Set<NotificationDispatchStep>();
    public DbSet<NotificationDispatchEvent> NotificationDispatchEvents => Set<NotificationDispatchEvent>();
    public DbSet<ArchivedNotification> ArchivedNotifications { get; set; } = default!;
    public DbSet<ArchivedNotificationDispatch> ArchivedNotificationDispatches => Set<ArchivedNotificationDispatch>();
    public DbSet<NotificationRecipient> NotificationRecipients { get; set; } = default!;
    public DbSet<NotificationDelivery> NotificationDeliveries { get; set; } = default!;
    public DbSet<NotificationDeliveryChronology> NotificationDeliveryChronology { get; set; } = default!;
    public DbSet<NotificationDeliveryLatencyRollup> NotificationDeliveryLatencyRollups { get; set; } = default!;
    public DbSet<NotificationDeliveryReceipt> NotificationDeliveryReceipts { get; set; } = default!;
    public DbSet<NotificationDigestBucket> NotificationDigestBuckets { get; set; } = default!;
    public DbSet<NotificationDigestItem> NotificationDigestItems { get; set; } = default!;
    public DbSet<UserNotification> UserNotifications { get; set; } = default!;
    public DbSet<NotificationOperationReceipt> NotificationOperationReceipts { get; set; } = default!;
    public DbSet<NotificationProviderConfig> NotificationProviderConfigs { get; set; } = default!;
    public DbSet<TenantNotificationPolicy> TenantNotificationPolicies { get; set; } = default!;
    public DbSet<UserNotificationPreference> UserNotificationPreferences { get; set; } = default!;
    public DbSet<NotificationPresenceRecord> NotificationPresenceRecords { get; set; } = default!;
    public DbSet<NotificationTemplateDefinition> NotificationTemplateDefinitions { get; set; } = default!;
    public DbSet<NotificationTemplateRevision> NotificationTemplateRevisions { get; set; } = default!;
    public DbSet<NotificationTemplateSample> NotificationTemplateSamples { get; set; } = default!;
    public DbSet<NotificationTemplateContentBlock> NotificationTemplateContentBlocks { get; set; } = default!;
    public DbSet<TenantNotificationSuppression> TenantNotificationSuppressions { get; set; } = default!;
    public DbSet<NotificationDeviceToken> NotificationDeviceTokens { get; set; } = default!;

    public NotificationsDbContextApp(DbContextOptions<NotificationsDbContextApp> options)
        : base(options)
    {
    }

    public Task AddUserNotificationAsync(UserNotification userNotification, CancellationToken cancellationToken = default)
    {
        return UserNotifications.AddAsync(userNotification, cancellationToken).AsTask();
    }

    public Task AddDeliveryReceiptAsync(NotificationDeliveryReceipt receipt, CancellationToken cancellationToken = default)
    {
        return NotificationDeliveryReceipts.AddAsync(receipt, cancellationToken).AsTask();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddTenantWrightMessagingEntities();
        modelBuilder.ConfigureNotifications();
        modelBuilder.ConfigureNotificationDispatches();
        modelBuilder.Entity<TenantNotificationSuppression>(b => { b.ToTable("TenantNotificationSuppressions"); b.HasKey(x => x.TenantId); b.Property(x => x.IsSuppressed).IsRequired(); b.Property(x => x.UpdatedAtUtc).IsRequired(); });
    }
}
