using AuditLogs.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Modeling;

namespace AuditLogs.EntityFrameworkCore.EntityFrameworkCore;

public static class AuditLogsDbContextModelCreatingExtensions
{
    public static void ConfigureAuditLogs(this ModelBuilder builder)
    {
        builder.Entity<AuditLog>(b =>
        {
            b.ToTable("AuditLogs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.EventId).IsRequired();
            b.Property(x => x.ApplicationName).IsRequired().HasMaxLength(AuditLogConsts.MaxApplicationNameLength);
            b.Property(x => x.Category).IsRequired().HasMaxLength(AuditLogConsts.MaxCategoryLength);
            b.Property(x => x.UserId).HasMaxLength(AuditLogConsts.MaxUserIdLength);
            b.Property(x => x.UserName).HasMaxLength(AuditLogConsts.MaxUserNameLength);
            b.Property(x => x.TenantName).HasMaxLength(AuditLogConsts.MaxTenantNameLength);
            b.Property(x => x.ImpersonatorUserId).HasMaxLength(AuditLogConsts.MaxUserIdLength);
            b.Property(x => x.ImpersonatorUserName).HasMaxLength(AuditLogConsts.MaxUserNameLength);
            b.Property(x => x.ImpersonatorTenantName).HasMaxLength(AuditLogConsts.MaxTenantNameLength);
            b.Property(x => x.ClientIpAddress).HasMaxLength(AuditLogConsts.MaxClientIpAddressLength);
            b.Property(x => x.ClientName).HasMaxLength(AuditLogConsts.MaxClientNameLength);
            b.Property(x => x.ClientId).HasMaxLength(AuditLogConsts.MaxClientIdLength);
            b.Property(x => x.CorrelationId).HasMaxLength(AuditLogConsts.MaxCorrelationIdLength);
            b.Property(x => x.TraceId).HasMaxLength(AuditLogConsts.MaxTraceIdLength);
            b.Property(x => x.BrowserInfo).HasMaxLength(AuditLogConsts.MaxBrowserInfoLength);
            b.Property(x => x.HttpMethod).HasMaxLength(AuditLogConsts.MaxHttpMethodLength);
            b.Property(x => x.Url).HasMaxLength(AuditLogConsts.MaxUrlLength);
            b.Property(x => x.Exceptions).HasMaxLength(AuditLogConsts.MaxExceptionPayloadLength);
            b.Property(x => x.Comments).HasMaxLength(AuditLogConsts.MaxCommentsLength);
            b.Property(x => x.Signature).HasMaxLength(AuditLogConsts.MaxSignatureLength);
            b.Property(x => x.SignatureKeyId).HasMaxLength(AuditLogConsts.MaxSignatureKeyIdLength);

            b.HasMany(x => x.Actions).WithOne().HasForeignKey(x => x.AuditLogId).IsRequired();
            b.HasMany(x => x.EntityChanges).WithOne().HasForeignKey(x => x.AuditLogId).IsRequired();

            b.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("IX_AuditLogs_EventId");
            b.HasIndex(x => new { x.TenantId, x.ExecutionTime }).HasDatabaseName("IX_AuditLogs_TenantId_ExecutionTime");
            b.HasIndex(x => new { x.TenantId, x.ExecutionTime, x.Id }).HasDatabaseName("IX_AuditLogs_TenantId_ExecutionTime_Id");
            b.HasIndex(x => new { x.TenantId, x.UserId, x.ExecutionTime }).HasDatabaseName("IX_AuditLogs_TenantId_UserId_ExecutionTime");
            b.HasIndex(x => x.CorrelationId).HasDatabaseName("IX_AuditLogs_CorrelationId");
            b.HasIndex(x => x.ExecutionTime).HasDatabaseName("IX_AuditLogs_ExecutionTime");
            b.HasIndex(x => new { x.ExecutionTime, x.Id }).HasDatabaseName("IX_AuditLogs_ExecutionTime_Id");
            // Category and application are both offered as filters on the audit console and neither had any
            // index behind it, so filtering by one meant scanning the tenant's whole log. ExecutionTime is the
            // third column because the list always orders by it, so one index serves the filter and the sort.
            b.HasIndex(x => new { x.TenantId, x.Category, x.ExecutionTime })
                .HasDatabaseName("IX_AuditLogs_TenantId_Category_ExecutionTime");
            b.HasIndex(x => new { x.TenantId, x.ApplicationName, x.ExecutionTime })
                .HasDatabaseName("IX_AuditLogs_TenantId_ApplicationName_ExecutionTime");
        });

        builder.Entity<AuditLogAction>(b =>
        {
            b.ToTable("AuditLogActions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.AuditLogId).IsRequired();
            b.Property(x => x.ServiceName).HasMaxLength(AuditLogConsts.MaxServiceNameLength).IsRequired();
            b.Property(x => x.MethodName).HasMaxLength(AuditLogConsts.MaxMethodNameLength).IsRequired();
            b.Property(x => x.Parameters).HasMaxLength(AuditLogConsts.MaxParametersLength).IsRequired();
            b.Property(x => x.Result).HasMaxLength(AuditLogConsts.MaxActionResultLength);

            b.HasIndex(x => x.AuditLogId).HasDatabaseName("IX_AuditLogActions_AuditLogId");
            b.HasIndex(x => new { x.TenantId, x.ServiceName, x.MethodName, x.ExecutionTime })
                .HasDatabaseName("IX_AuditLogActions_Tenant_Service_Method_ExecutionTime");
        });

        builder.Entity<EntityChange>(b =>
        {
            b.ToTable("EntityChanges");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.AuditLogId).IsRequired();
            b.Property(x => x.ChangeType).HasMaxLength(AuditLogConsts.MaxEntityChangeTypeLength).IsRequired();
            b.Property(x => x.EntityId).HasMaxLength(AuditLogConsts.MaxEntityIdLength).IsRequired();
            b.Property(x => x.EntityTypeFullName).HasMaxLength(AuditLogConsts.MaxEntityTypeFullNameLength).IsRequired();

            b.HasMany(x => x.PropertyChanges).WithOne().HasForeignKey(x => x.EntityChangeId).IsRequired();

            b.HasIndex(x => x.AuditLogId).HasDatabaseName("IX_EntityChanges_AuditLogId");
            b.HasIndex(x => new { x.TenantId, x.EntityTypeFullName, x.EntityId })
                .HasDatabaseName("IX_EntityChanges_Tenant_EntityType_EntityId");
            b.HasIndex(x => x.ChangeTime).HasDatabaseName("IX_EntityChanges_ChangeTime");
            // The entity-changes cursor pages on `(ChangeTime, Id)` within a tenant, exactly as the main log
            // does — and the main log has `IX_AuditLogs_TenantId_ExecutionTime_Id` for it while this had only a
            // tenant-less ChangeTime index, leaving the more selective of the two cursors the less covered.
            b.HasIndex(x => new { x.TenantId, x.ChangeTime, x.Id })
                .HasDatabaseName("IX_EntityChanges_TenantId_ChangeTime_Id");
        });

        builder.Entity<EntityPropertyChange>(b =>
        {
            b.ToTable("EntityPropertyChanges");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.EntityChangeId).IsRequired();
            b.Property(x => x.NewValue).HasMaxLength(AuditLogConsts.MaxNewValueLength);
            b.Property(x => x.OriginalValue).HasMaxLength(AuditLogConsts.MaxOriginalValueLength);
            b.Property(x => x.PropertyName).HasMaxLength(AuditLogConsts.MaxPropertyNameLength).IsRequired();
            b.Property(x => x.PropertyTypeFullName).HasMaxLength(AuditLogConsts.MaxPropertyTypeFullNameLength);

            b.HasIndex(x => x.EntityChangeId).HasDatabaseName("IX_EntityPropertyChanges_EntityChangeId");
        });

        builder.Entity<AuditLogExport>(b =>
        {
            b.ToTable("AuditLogExports");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.RequestedByUserId).HasMaxLength(AuditLogConsts.MaxUserIdLength);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.Format).IsRequired();
            b.Property(x => x.FilterSnapshotJson).IsRequired().HasMaxLength(AuditLogConsts.MaxExportFilterSnapshotLength);
            b.Property(x => x.MaxRowCount).IsRequired();
            b.Property(x => x.ChunkSize).IsRequired();
            b.Property(x => x.RequestedAtUtc).IsRequired();
            b.Property(x => x.ExpireAtUtc).IsRequired();
            b.Property(x => x.DownloadTokenHash).HasMaxLength(AuditLogConsts.MaxExportDownloadTokenHashLength);
            b.Property(x => x.DownloadTokenExpiresAtUtc);
            b.Property(x => x.FileName).HasMaxLength(AuditLogConsts.MaxExportFileNameLength);
            b.Property(x => x.ContentType).HasMaxLength(AuditLogConsts.MaxExportContentTypeLength);
            b.Property(x => x.FileContent);
            b.Property(x => x.FailureReason).HasMaxLength(AuditLogConsts.MaxExportFailureReasonLength);

            b.HasIndex(x => new { x.TenantId, x.Status, x.CreationTime })
                .HasDatabaseName("IX_AuditLogExports_TenantId_Status_CreationTime");
            b.HasIndex(x => new { x.Status, x.NextAttemptTimeUtc })
                .HasDatabaseName("IX_AuditLogExports_Status_NextAttemptTimeUtc");
            b.HasIndex(x => x.ExpireAtUtc)
                .HasDatabaseName("IX_AuditLogExports_ExpireAtUtc");
        });

        builder.Entity<AuditArchiveManifest>(b =>
        {
            b.ToTable("AuditArchiveManifests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.PartitionKey)
                .HasMaxLength(AuditLogConsts.MaxArchivePartitionKeyLength)
                .IsRequired();
            b.Property(x => x.ArchiveUri)
                .HasMaxLength(AuditLogConsts.MaxArchiveUriLength)
                .IsRequired();
            b.Property(x => x.RowCount).IsRequired();
            b.Property(x => x.Checksum)
                .HasMaxLength(AuditLogConsts.MaxArchiveChecksumLength)
                .IsRequired();
            b.Property(x => x.ArchivedAtUtc).IsRequired();
            b.Property(x => x.DeleteAfterUtc).IsRequired();
            b.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(AuditLogConsts.MaxArchiveStatusLength)
                .IsRequired();

            b.HasIndex(x => x.PartitionKey)
                .IsUnique()
                .HasDatabaseName("IX_AuditArchiveManifests_PartitionKey");
            b.HasIndex(x => new { x.Status, x.ArchivedAtUtc })
                .HasDatabaseName("IX_AuditArchiveManifests_Status_ArchivedAtUtc");
        });

        builder.Entity<AuditLogSeal>(b =>
        {
            b.ToTable("AuditLogSeals");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.FromId).IsRequired();
            b.Property(x => x.ToId).IsRequired();
            b.Property(x => x.RowCount).IsRequired();
            b.Property(x => x.PrevSealHash).IsRequired().HasMaxLength(AuditLogSealConsts.MaxHashLength);
            b.Property(x => x.BatchHash).IsRequired().HasMaxLength(AuditLogSealConsts.MaxHashLength);
            b.Property(x => x.Signature).IsRequired().HasMaxLength(AuditLogSealConsts.MaxSignatureLength);
            b.Property(x => x.SignatureKeyId).IsRequired().HasMaxLength(AuditLogSealConsts.MaxSignatureKeyIdLength);

            // Seals must never overlap and must be appended strictly in increasing Id-range order,
            // so ToId is unique (one seal can't claim the same right-edge twice) and both bounds are indexed
            // for efficient "find the tail seal" / "find seals covering this range" lookups.
            b.HasIndex(x => x.ToId).IsUnique().HasDatabaseName("IX_AuditLogSeals_ToId");
            b.HasIndex(x => x.FromId).HasDatabaseName("IX_AuditLogSeals_FromId");
        });

        builder.Entity<AuditLogLegalHold>(b =>
        {
            b.ToTable("AuditLogLegalHolds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(AuditLogLegalHoldConsts.MaxNameLength);
            b.Property(x => x.UserId).HasMaxLength(AuditLogConsts.MaxUserIdLength);
            b.Property(x => x.Reason).HasMaxLength(AuditLogLegalHoldConsts.MaxReasonLength);

            b.HasIndex(x => new { x.IsReleased, x.TenantId }).HasDatabaseName("IX_AuditLogLegalHolds_IsReleased_TenantId");
        });

        builder.Entity<AuditArchiveRestoreJob>(b =>
        {
            b.ToTable("AuditArchiveRestoreJobs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.ManifestId).IsRequired();
            b.Property(x => x.PartitionKey).IsRequired().HasMaxLength(AuditLogConsts.MaxArchivePartitionKeyLength);
            b.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(AuditLogConsts.MaxArchiveStatusLength);
            b.Property(x => x.Reason).HasMaxLength(AuditLogConsts.MaxArchiveFailureReasonLength);
            b.Property(x => x.RequestedByUserId).HasMaxLength(AuditLogConsts.MaxUserIdLength);
            b.Property(x => x.RequestedAtUtc).IsRequired();
            b.Property(x => x.FailureReason).HasMaxLength(AuditLogConsts.MaxArchiveFailureReasonLength);
            b.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(AuditLogConsts.MaxArchiveRestoreIdempotencyKeyLength);

            b.HasIndex(x => new { x.ManifestId, x.Status, x.RequestedAtUtc })
                .HasDatabaseName("IX_AuditArchiveRestoreJobs_Manifest_Status_RequestedAtUtc");
            b.HasIndex(x => new { x.Status, x.RequestedAtUtc })
                .HasDatabaseName("IX_AuditArchiveRestoreJobs_Status_RequestedAtUtc");
            b.HasIndex(x => x.IdempotencyKey)
                .HasDatabaseName("IX_AuditArchiveRestoreJobs_IdempotencyKey");
        });

        builder.Entity<AuditLogSealVerificationRun>(b =>
        {
            b.ToTable("AuditLogSealVerificationRuns");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();

            b.ConfigureByConvention();

            b.Property(x => x.FromId).IsRequired();
            b.Property(x => x.ToId).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(AuditLogConsts.MaxArchiveStatusLength);
            b.Property(x => x.IssuesJson)
                .HasColumnType("jsonb")
                .HasMaxLength(AuditLogConsts.MaxSealVerificationIssuesLength)
                .IsRequired();
            b.Property(x => x.RequestedByUserId).HasMaxLength(AuditLogConsts.MaxUserIdLength);
            b.Property(x => x.RequestedAtUtc).IsRequired();

            b.HasIndex(x => new { x.Status, x.RequestedAtUtc })
                .HasDatabaseName("IX_AuditLogSealVerificationRuns_Status_RequestedAtUtc");
            b.HasIndex(x => x.RequestedAtUtc)
                .HasDatabaseName("IX_AuditLogSealVerificationRuns_RequestedAtUtc");
        });
    }
}
