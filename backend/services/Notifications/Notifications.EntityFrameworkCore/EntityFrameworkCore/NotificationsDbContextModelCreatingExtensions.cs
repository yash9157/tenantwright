using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Archival;
using Notifications.Domain.Digests;
using Notifications.Domain.Devices;
using Notifications.Domain.Notifications;
using Notifications.Domain.Policy;
using Notifications.Domain.Presence;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Notifications.Domain.Templates;
using Shared.EntityFrameworkCore.Modeling;

namespace Notifications.EntityFrameworkCore.EntityFrameworkCore;

public static class NotificationsDbContextModelCreatingExtensions
{
    public static void ConfigureNotifications(this ModelBuilder builder)
    {
        builder.Entity<Notification>(b =>
        {
            b.ToTable("Notifications");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.RequestId).IsRequired();
            b.Property(x => x.Channel).IsRequired();
            b.Property(x => x.TemplateName).IsRequired().HasMaxLength(256);
            b.Property(x => x.TemplateModelJson).IsRequired();
            b.Property(x => x.SourceService).IsRequired().HasMaxLength(128);
            b.Property(x => x.Subject).HasMaxLength(256);
            b.Property(x => x.Category).HasMaxLength(128);
            b.Property(x => x.Priority).IsRequired();
            b.Property(x => x.ActionUrl).HasMaxLength(2048);
            b.Property(x => x.ScheduledForUtc);
            b.Property(x => x.PreferenceUserId).HasMaxLength(128);

            b.HasIndex(x => new { x.TenantId, x.Channel, x.CreationTime });
            b.HasIndex(x => new { x.CreationTime, x.Id })
                .HasDatabaseName("IX_Notifications_CreationTime_Id");
            b.HasIndex(x => new { x.TenantId, x.Category });
            b.HasIndex(x => new { x.TenantId, x.SourceService, x.CreationTime });
            b.HasIndex(x => new { x.TenantId, x.RequestId })
                .IsUnique()
                .HasDatabaseName("IX_Notifications_TenantId_RequestId");
        });

        builder.Entity<ArchivedNotification>(b =>
        {
            b.ToTable("ArchivedNotifications");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId);
            b.Property(x => x.SourceNotificationId).IsRequired();
            b.Property(x => x.SourceCreatedAtUtc).IsRequired();
            b.Property(x => x.ArchivedAtUtc).IsRequired();
            b.Property(x => x.PayloadJson).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.PayloadSha256).IsRequired().HasMaxLength(64).IsFixedLength();
            b.HasIndex(x => x.SourceNotificationId).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SourceCreatedAtUtc, x.Id })
                .HasDatabaseName("IX_ArchivedNotifications_TenantId_SourceCreatedAtUtc_Id");
        });

        builder.Entity<NotificationRecipient>(b =>
        {
            b.ToTable("NotificationRecipients");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.NotificationId).IsRequired();
            b.Property(x => x.RecipientType).IsRequired();
            b.Property(x => x.RecipientValue).IsRequired().HasMaxLength(4096);
            b.Property(x => x.RecipientValueHash).IsRequired().HasMaxLength(64);

            b.HasIndex(x => x.NotificationId);
            b.HasIndex(x => new { x.NotificationId, x.RecipientType, x.RecipientValueHash }).IsUnique();
        });

        builder.Entity<NotificationDelivery>(b =>
        {
            b.ToTable("NotificationDeliveries");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.NotificationId).IsRequired();
            b.Property(x => x.RecipientId).IsRequired();
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.ProviderMessageId).HasMaxLength(256);
            b.Property(x => x.ProviderConfigId);
            b.Property(x => x.LastError).HasMaxLength(2048);
            b.Property(x => x.TryCount).IsRequired();
            b.Property(x => x.LastTryTimeUtc);
            b.Property(x => x.SentTimeUtc);
            b.Property(x => x.NextAttemptTimeUtc);
            b.Property(x => x.NotBeforeUtc);
            b.Property(x => x.PolicyHeldUntilUtc);
            b.Property(x => x.DeliveryAttemptId);
            b.Property(x => x.DeliveryAttemptedAtUtc);

            b.HasIndex(x => new { x.NotificationId, x.RecipientId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreationTime });
            b.HasIndex(x => new { x.Status, x.NextAttemptTimeUtc, x.CreationTime })
                .HasDatabaseName("IX_NotificationDeliveries_Status_NextAttempt_CreationTime");
            b.HasIndex(x => new { x.Status, x.SentTimeUtc })
                .HasDatabaseName("IX_NotificationDeliveries_Status_SentTimeUtc");
            b.HasIndex(x => new { x.NotificationId, x.CreationTime })
                .HasDatabaseName("IX_NotificationDeliveries_NotificationId_CreationTime");
            b.HasIndex(x => new { x.ProviderConfigId, x.ProviderMessageId })
                .HasDatabaseName("IX_NotificationDeliveries_ProviderConfigId_ProviderMessageId")
                .IsUnique()
                .HasFilter("\"ProviderConfigId\" IS NOT NULL AND \"ProviderMessageId\" IS NOT NULL");
        });

        builder.Entity<NotificationDeliveryLatencyRollup>(b =>
        {
            b.ToTable("NotificationDeliveryLatencyRollups");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.TenantId);
            b.Property(x => x.ScopeKey).IsRequired().HasMaxLength(36);
            b.Property(x => x.Channel).IsRequired();
            b.Property(x => x.BucketUtc).IsRequired();
            b.Property(x => x.SampleCount).IsRequired();
            b.Property(x => x.P50Ms).IsRequired();
            b.Property(x => x.P95Ms).IsRequired();
            b.Property(x => x.P99Ms).IsRequired();
            b.Property(x => x.CalculatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.ScopeKey, x.Channel, x.BucketUtc })
                .HasDatabaseName("IX_NotificationDeliveryLatencyRollups_Scope_Channel_Bucket")
                .IsUnique();
            b.HasIndex(x => new { x.TenantId, x.BucketUtc })
                .HasDatabaseName("IX_NotificationDeliveryLatencyRollups_Tenant_Bucket");
        });

        builder.Entity<NotificationDeliveryChronology>(b =>
        {
            b.ToTable("NotificationDeliveryChronology");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.DeliveryId).IsRequired();
            b.Property(x => x.EventKind).IsRequired();
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.ProviderMessageId).HasMaxLength(NotificationDeliveryChronology.MaxProviderMessageIdLength);
            b.Property(x => x.ResponseCode).HasMaxLength(NotificationDeliveryChronology.MaxResponseCodeLength);
            b.Property(x => x.SafeError).HasMaxLength(NotificationDeliveryChronology.MaxSafeErrorLength);
            b.Property(x => x.LatencyMs);
            b.Property(x => x.ActorId).HasMaxLength(NotificationDeliveryChronology.MaxActorIdLength);
            b.Property(x => x.ActorType).IsRequired();
            b.Property(x => x.OccurredAtUtc).IsRequired();

            b.HasIndex(x => new { x.DeliveryId, x.OccurredAtUtc, x.Id })
                .HasDatabaseName("IX_NotificationDeliveryChronology_DeliveryId_OccurredAtUtc_Id");
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc, x.Id })
                .HasDatabaseName("IX_NotificationDeliveryChronology_TenantId_OccurredAtUtc_Id");

            b.HasOne(x => x.Delivery)
                .WithMany(x => x.Chronology)
                .HasForeignKey(x => x.DeliveryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserNotification>(b =>
        {
            b.ToTable("UserNotifications");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.NotificationId).IsRequired();
            b.Property(x => x.UserId).IsRequired().HasMaxLength(128);
            b.Property(x => x.IsRead).IsRequired();
            b.Property(x => x.ReadTimeUtc);
            b.Property(x => x.IsArchived).IsRequired();
            b.Property(x => x.ArchivedTimeUtc);
            b.Property(x => x.Revision).IsRequired().IsConcurrencyToken();
            b.Property(x => x.Title);
            b.Property(x => x.Summary);
            b.Property(x => x.Body);

            b.HasIndex(x => new { x.NotificationId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.IsRead, x.CreationTime });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.IsArchived, x.CreationTime })
                .HasDatabaseName("IX_UserNotifications_TenantId_UserId_IsArchived_CreationTime");
            b.HasIndex(x => new { x.TenantId, x.UserId, x.CreationTime })
                .HasDatabaseName("IX_UserNotifications_TenantId_UserId_CreationTime");
        });

        builder.Entity<NotificationOperationReceipt>(b =>
        {
            b.ToTable("NotificationOperationReceipts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TenantId);
            b.Property(x => x.ScopeKey).IsRequired().HasMaxLength(64);
            b.Property(x => x.ActorUserId).IsRequired().HasMaxLength(128);
            b.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            b.Property(x => x.Operation).IsRequired().HasMaxLength(64);
            b.Property(x => x.TargetKey).IsRequired().HasMaxLength(128);
            b.Property(x => x.RequestHash).IsRequired().HasMaxLength(64).IsFixedLength();
            b.Property(x => x.ResultingRevision).IsRequired();
            b.Property(x => x.AffectedCount).IsRequired();
            b.Property(x => x.ResponseJson).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.AppliedAtUtc).IsRequired();
            b.HasIndex(x => new { x.ScopeKey, x.ActorUserId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.ScopeKey, x.Operation, x.TargetKey, x.AppliedAtUtc });
        });

        builder.Entity<NotificationDeliveryReceipt>(b =>
        {
            b.ToTable("NotificationDeliveryReceipts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.DeliveryId).IsRequired();
            b.Property(x => x.ProviderConfigId);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(64);
            b.Property(x => x.ProviderEventId).IsRequired().HasMaxLength(256);
            b.Property(x => x.EventType).IsRequired();
            b.Property(x => x.ProviderCode).HasMaxLength(64);
            b.Property(x => x.SafeReason).HasMaxLength(512);
            b.Property(x => x.OccurredAtUtc).IsRequired();
            b.Property(x => x.ReceivedAtUtc).IsRequired();

            // Idempotency: a provider event id is unique per provider.
            b.HasIndex(x => new { x.ProviderName, x.ProviderEventId }).IsUnique();
            b.HasIndex(x => new { x.DeliveryId, x.ReceivedAtUtc });
        });

        builder.Entity<NotificationDigestBucket>(b =>
        {
            b.ToTable("NotificationDigestBuckets");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId);
            b.Property(x => x.ScopeKey).IsRequired().HasMaxLength(36);
            b.Property(x => x.Channel).IsRequired();
            b.Property(x => x.RecipientType).IsRequired();
            b.Property(x => x.RecipientValue).IsRequired().HasMaxLength(4096);
            b.Property(x => x.RecipientValueHash).IsRequired().HasMaxLength(64);
            b.Property(x => x.Frequency).IsRequired();
            b.Property(x => x.PeriodStartUtc).IsRequired();
            b.Property(x => x.PeriodEndUtc).IsRequired();
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.AttemptCount).IsRequired();
            b.Property(x => x.EmittedAtUtc);
            b.Property(x => x.EmittedNotificationId);
            b.HasIndex(x => new { x.ScopeKey, x.Channel, x.RecipientType, x.RecipientValueHash, x.Frequency, x.PeriodStartUtc })
                .HasDatabaseName("IX_NotificationDigestBuckets_Scope_Channel_Recipient_Period")
                .IsUnique();
            b.HasIndex(x => new { x.Status, x.PeriodEndUtc, x.Id });
        });

        builder.Entity<NotificationDigestItem>(b =>
        {
            b.ToTable("NotificationDigestItems");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.ConfigureByConvention();
            b.Property(x => x.TenantId);
            b.Property(x => x.BucketId).IsRequired();
            b.Property(x => x.SourceNotificationId).IsRequired();
            b.Property(x => x.TemplateName).IsRequired().HasMaxLength(256);
            b.Property(x => x.Category).HasMaxLength(128);
            b.Property(x => x.AddedAtUtc).IsRequired();
            b.HasIndex(x => new { x.BucketId, x.SourceNotificationId }).IsUnique();
            b.HasOne<NotificationDigestBucket>()
                .WithMany()
                .HasForeignKey(x => x.BucketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TenantNotificationPolicy>(b =>
        {
            b.ToTable("TenantNotificationPolicies");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.IanaTimeZoneId).IsRequired().HasMaxLength(128);
            b.Property(x => x.QuietHoursEnabled).IsRequired();
            b.Property(x => x.QuietStartLocalMinutes).IsRequired();
            b.Property(x => x.QuietEndLocalMinutes).IsRequired();
            b.Property(x => x.QuietChannels).IsRequired();
            b.Property(x => x.BypassCategoriesJson).IsRequired().HasColumnType("jsonb").HasDefaultValue("[]");
            b.Property(x => x.DigestFrequency).IsRequired();
            b.Property(x => x.Revision).IsRequired().IsConcurrencyToken();
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        builder.Entity<UserNotificationPreference>(b =>
        {
            b.ToTable("UserNotificationPreferences");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId);
            b.Property(x => x.ScopeKey).IsRequired().HasMaxLength(36);
            b.Property(x => x.UserId).IsRequired().HasMaxLength(128);
            b.Property(x => x.QuietHoursEnabled).IsRequired();
            b.Property(x => x.IanaTimeZoneId).IsRequired().HasMaxLength(128);
            b.Property(x => x.QuietStartLocalMinutes).IsRequired();
            b.Property(x => x.QuietEndLocalMinutes).IsRequired();
            b.Property(x => x.QuietChannels).IsRequired();
            b.Property(x => x.DigestFrequency);
            b.Property(x => x.Revision).IsRequired().IsConcurrencyToken();
            b.HasIndex(x => new { x.ScopeKey, x.UserId }).IsUnique();
        });

        builder.Entity<NotificationProviderConfig>(b =>
        {
            b.ToTable("NotificationProviderConfigs");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            // A concrete key gives PostgreSQL and SQLite the same NULL-safe unique-index behavior.
            // It is initialized from TenantId in the domain constructor and is immutable afterwards.
            b.Property(x => x.ScopeKey).IsRequired().HasMaxLength(36);
            b.Property(x => x.Channel).IsRequired();
            b.Property(x => x.ProviderType).IsRequired().HasMaxLength(64);
            b.Property(x => x.Name).IsRequired().HasMaxLength(128);
            b.Property(x => x.IsActive).IsRequired();
            b.Property(x => x.IsDefault).IsRequired();
            b.Property(x => x.Priority).IsRequired();
            b.Property(x => x.SettingsJson).IsRequired().HasColumnType("jsonb").HasDefaultValue("{}");
            b.Property(x => x.SecretEnvelope);
            b.Property(x => x.SecretFingerprint).HasMaxLength(32);
            b.Property(x => x.SecretProtectionKeyId).HasMaxLength(128);
            b.Property(x => x.ComplianceContact).HasMaxLength(256);
            b.Property(x => x.LastTestedAtUtc);
            b.Property(x => x.LastTestResult).HasMaxLength(64);
            b.Property(x => x.VerificationState).IsRequired();
            b.Property(x => x.LiveVerifiedAtUtc);
            b.Property(x => x.LiveVerificationReference).HasMaxLength(128);

            b.HasIndex(x => new { x.ScopeKey, x.Channel, x.Name })
                .HasDatabaseName("IX_NotificationProviderConfigs_ScopeKey_Channel_Name")
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false");
            b.HasIndex(x => new { x.ScopeKey, x.Channel, x.IsDefault })
                .HasDatabaseName("IX_NotificationProviderConfigs_ScopeKey_Channel_IsDefault_Unique")
                .IsUnique()
                .HasFilter("\"IsDefault\" = TRUE AND \"IsDeleted\" = false");
        });

        builder.Entity<NotificationDeviceToken>(b =>
        {
            b.ToTable("NotificationDeviceTokens");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.UserId).IsRequired().HasMaxLength(128);
            b.Property(x => x.DeviceToken).IsRequired().HasMaxLength(NotificationDeviceToken.MaxProtectedDeviceTokenLength);
            // Nullable only while legacy plaintext registrations are lazily upgraded. New writes
            // always set the hash; a future cleanup migration can make this required.
            b.Property(x => x.DeviceTokenHash).HasMaxLength(64);
            b.Property(x => x.Platform).IsRequired();
            b.Property(x => x.AppIdentifier).HasMaxLength(128);
            b.Property(x => x.IsActive).IsRequired();
            b.Property(x => x.LastSeenAtUtc).IsRequired();

            b.HasIndex(x => new { x.TenantId, x.DeviceTokenHash })
                .HasDatabaseName("IX_NotificationDeviceTokens_TenantId_DeviceTokenHash")
                .IsUnique()
                .HasFilter("\"DeviceTokenHash\" IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.UserId, x.IsActive })
                .HasDatabaseName("IX_NotificationDeviceTokens_TenantId_UserId_IsActive");
        });

        builder.Entity<NotificationPresenceRecord>(b =>
        {
            b.ToTable("NotificationPresenceRecords");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.UserId).IsRequired().HasMaxLength(128);
            b.Property(x => x.LastSeenAtUtc).IsRequired();

            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LastSeenAtUtc });
        });

        builder.Entity<NotificationTemplateDefinition>(b =>
        {
            b.ToTable("NotificationTemplateDefinitions");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(256);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
            b.Property(x => x.Category).IsRequired().HasMaxLength(128);
            b.Property(x => x.RelativePath).IsRequired().HasMaxLength(512);
            b.Property(x => x.DraftSubjectTemplate);
            b.Property(x => x.DraftBodyTemplate).IsRequired();
            b.Property(x => x.PublishedRevisionId);
            b.Property(x => x.PublishedRevisionNumber);
            b.Property(x => x.PublishedAtUtc);
            b.Property(x => x.IsArchived).IsRequired();
            b.Property(x => x.ArchivedAtUtc);
            b.Property(x => x.ArchivedByUserId);

            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => new { x.Category, x.DisplayName });
            b.HasIndex(x => x.IsArchived);
        });

        builder.Entity<NotificationTemplateRevision>(b =>
        {
            b.ToTable("NotificationTemplateRevisions");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TemplateId).IsRequired();
            b.Property(x => x.RevisionNumber).IsRequired();
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
            b.Property(x => x.Category).IsRequired().HasMaxLength(128);
            b.Property(x => x.SubjectTemplate);
            b.Property(x => x.BodyTemplate).IsRequired();

            b.HasIndex(x => new { x.TemplateId, x.RevisionNumber }).IsUnique();
            b.HasIndex(x => new { x.TemplateId, x.CreationTime })
                .HasDatabaseName("IX_NotificationTemplateRevisions_TemplateId_CreationTime");
        });

        builder.Entity<NotificationTemplateSample>(b =>
        {
            b.ToTable("NotificationTemplateSamples");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TemplateId).IsRequired();
            b.Property(x => x.Name).IsRequired().HasMaxLength(128);
            b.Property(x => x.Description).HasMaxLength(512);
            b.Property(x => x.ModelJson).IsRequired();
            b.Property(x => x.IsDefault).IsRequired();

            b.HasIndex(x => new { x.TemplateId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TemplateId, x.IsDefault });
        });

        builder.Entity<NotificationTemplateContentBlock>(b =>
        {
            b.ToTable("NotificationTemplateContentBlocks");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.Key).IsRequired().HasMaxLength(128);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
            b.Property(x => x.Category).IsRequired().HasMaxLength(128);
            b.Property(x => x.Description).HasMaxLength(512);
            b.Property(x => x.SubjectSnippet).HasMaxLength(2048);
            b.Property(x => x.BodySnippet).HasMaxLength(4096);
            b.Property(x => x.IsArchived).IsRequired();
            b.Property(x => x.ArchivedAtUtc);
            b.Property(x => x.ArchivedByUserId);

            b.HasIndex(x => x.Key).IsUnique();
            b.HasIndex(x => new { x.IsArchived, x.Category, x.DisplayName });
        });

        builder.Entity<NotificationRecipient>()
            .HasOne(x => x.Notification)
            .WithMany(x => x.Recipients)
            .HasForeignKey(x => x.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<NotificationDelivery>()
            .HasOne(x => x.Notification)
            .WithMany(x => x.Deliveries)
            .HasForeignKey(x => x.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<NotificationDelivery>()
            .HasOne(x => x.Recipient)
            .WithMany()
            .HasForeignKey(x => x.RecipientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<NotificationDeliveryReceipt>()
            .HasOne<NotificationDelivery>()
            .WithMany()
            .HasForeignKey(x => x.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<UserNotification>()
            .HasOne(x => x.Notification)
            .WithMany(x => x.UserNotifications)
            .HasForeignKey(x => x.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<NotificationTemplateRevision>()
            .HasOne<NotificationTemplateDefinition>()
            .WithMany()
            .HasForeignKey(x => x.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<NotificationTemplateSample>()
            .HasOne<NotificationTemplateDefinition>()
            .WithMany()
            .HasForeignKey(x => x.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
