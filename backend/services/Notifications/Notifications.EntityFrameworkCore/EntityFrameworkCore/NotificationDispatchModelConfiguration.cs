using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Archival;
using Notifications.Domain.Notifications;
using Shared.EntityFrameworkCore.Modeling;

namespace Notifications.EntityFrameworkCore.EntityFrameworkCore;

internal static class NotificationDispatchModelConfiguration
{
    private const string ScopeConstraint = "\"ScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000') AND " +
        "(\"TenantId\" IS NULL OR \"TenantId\" <> '00000000-0000-0000-0000-000000000000')";

    public static void ConfigureNotificationDispatches(this ModelBuilder builder)
    {
        builder.Entity<ArchivedNotificationDispatch>(entity =>
        {
            entity.ToTable("ArchivedNotificationDispatches", table => table.HasCheckConstraint("CK_ArchivedNotificationDispatches_Scope", ScopeConstraint));
            entity.HasKey(archive => archive.Id);
            entity.HasIndex(archive => new { archive.ScopeId, archive.SourceDispatchId, archive.SourceEventId }).IsUnique();
            entity.Property(archive => archive.PayloadJson).IsRequired();
            entity.Property(archive => archive.PayloadSha256).IsRequired().HasMaxLength(64);
        });
        builder.Entity<NotificationDispatch>(entity =>
        {
            entity.ToTable("NotificationDispatches", table =>
            {
                table.HasCheckConstraint("CK_NotificationDispatches_Scope", ScopeConstraint);
                table.HasCheckConstraint("CK_NotificationDispatches_Lifetime", "\"ExpiresAtUtc\" > \"ActivationTimeUtc\"");
            });
            entity.HasKey(dispatch => dispatch.Id);
            entity.ConfigureByConvention();
            entity.HasAlternateKey(dispatch => new { dispatch.ScopeId, dispatch.Id });
            entity.HasIndex(dispatch => new { dispatch.ScopeId, dispatch.RequestId }).IsUnique();
            entity.HasIndex(dispatch => new { dispatch.NextEvaluationAtUtc, dispatch.Id });
            entity.Property(dispatch => dispatch.RequestFingerprint).IsRequired().HasMaxLength(64);
            entity.Property(dispatch => dispatch.SourceService).IsRequired().HasMaxLength(128);
            entity.Property(dispatch => dispatch.Revision).IsConcurrencyToken();
            entity.HasMany(dispatch => dispatch.Recipients).WithOne()
                .HasForeignKey(recipient => new { recipient.ScopeId, recipient.DispatchId })
                .HasPrincipalKey(dispatch => new { dispatch.ScopeId, dispatch.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(dispatch => dispatch.Events).WithOne()
                .HasForeignKey(history => new { history.ScopeId, history.DispatchId })
                .HasPrincipalKey(dispatch => new { dispatch.ScopeId, dispatch.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationDispatchRecipient>(entity =>
        {
            entity.ToTable("NotificationDispatchRecipients", table => table.HasCheckConstraint("CK_NotificationDispatchRecipients_Scope", ScopeConstraint));
            entity.HasKey(recipient => recipient.Id);
            entity.HasAlternateKey(recipient => new { recipient.ScopeId, recipient.DispatchId, recipient.Id });
            entity.HasIndex(recipient => new { recipient.ScopeId, recipient.DispatchId, recipient.RecipientKey }).IsUnique();
            entity.Property(recipient => recipient.RecipientKey).IsRequired().HasMaxLength(128);
            entity.HasMany(recipient => recipient.Steps).WithOne()
                .HasForeignKey(step => new { step.ScopeId, step.DispatchId, step.RecipientId })
                .HasPrincipalKey(recipient => new { recipient.ScopeId, recipient.DispatchId, recipient.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationDispatchStep>(entity =>
        {
            entity.ToTable("NotificationDispatchSteps", table =>
            {
                table.HasCheckConstraint("CK_NotificationDispatchSteps_Scope", ScopeConstraint);
                table.HasCheckConstraint("CK_NotificationDispatchSteps_Ordinal", "\"Ordinal\" >= 0 AND \"Ordinal\" < 5");
            });
            entity.HasKey(step => step.Id);
            entity.HasAlternateKey(step => new { step.ScopeId, step.Id });
            entity.HasIndex(step => new { step.ScopeId, step.RecipientId, step.Ordinal }).IsUnique();
            entity.HasIndex(step => new { step.ScopeId, step.RecipientId, step.Channel }).IsUnique();
            entity.Property(step => step.ContentJson).IsRequired().HasMaxLength(262144);
            entity.Property(step => step.OutcomeReason).HasMaxLength(128);
        });

        builder.Entity<NotificationDispatchEvent>(entity =>
        {
            entity.ToTable("NotificationDispatchEvents", table => table.HasCheckConstraint("CK_NotificationDispatchEvents_Scope", ScopeConstraint));
            entity.HasKey(history => history.Id);
            entity.HasIndex(history => new { history.ScopeId, history.DispatchId, history.Revision }).IsUnique();
            entity.Property(history => history.Reason).HasMaxLength(128);
            entity.Property(history => history.ActorId).HasMaxLength(128);
        });

        builder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications", table => table.HasCheckConstraint("CK_Notifications_DispatchScope",
                "(\"DispatchStepId\" IS NULL AND \"DispatchScopeId\" IS NULL) OR " +
                "(\"DispatchStepId\" IS NOT NULL AND \"DispatchScopeId\" IS NOT NULL AND " +
                "\"DispatchScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000'))"));
            entity.HasIndex(notification => notification.DispatchStepId).IsUnique().HasFilter("\"DispatchStepId\" IS NOT NULL");
            entity.HasOne<NotificationDispatchStep>().WithOne()
                .HasForeignKey<Notification>(notification => new { notification.DispatchScopeId, notification.DispatchStepId })
                .HasPrincipalKey<NotificationDispatchStep>(step => new { step.ScopeId, step.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
