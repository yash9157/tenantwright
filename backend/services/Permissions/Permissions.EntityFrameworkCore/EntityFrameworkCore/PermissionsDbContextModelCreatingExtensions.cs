using Microsoft.EntityFrameworkCore;
using Permissions.Domain.Catalog;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.Features;
using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.EntityFrameworkCore.Modeling;

namespace Permissions.EntityFrameworkCore.EntityFrameworkCore;

public static class PermissionsDbContextModelCreatingExtensions
{
    public static void ConfigurePermissions(this ModelBuilder builder)
    {
        ConfigurePermissionDelegationDefinitions(builder);

        builder.Entity<PermissionGrant>(b =>
        {
            b.ToTable("PermissionGrants");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.Name).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderKeyLength);
            b.Property(x => x.Effect).IsRequired();
            b.Property(x => x.CreationTimeUtc).IsRequired();
            b.Property(x => x.ExpiresAtUtc);
            b.Property(x => x.Reason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.TicketId).HasMaxLength(PermissionGrantConsts.MaxTicketIdLength);
            b.Property(x => x.GrantedByUserId);

            b.HasIndex(x => new { x.ProviderName, x.ProviderKey, x.Name })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NULL");
            b.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey, x.Name })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey });
            b.HasIndex(x => new { x.TenantId, x.Name });
            b.HasIndex(x => x.ExpiresAtUtc);
        });

        builder.Entity<PermissionAssignmentState>(b =>
        {
            b.ToTable("PermissionAssignmentStates");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderKeyLength);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.ProviderName, x.ProviderKey }).IsUnique().HasFilter("\"TenantId\" IS NULL");
            b.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey }).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
        });

        builder.Entity<PermissionAssignmentOperation>(b =>
        {
            b.ToTable("PermissionAssignmentOperations");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderKeyLength);
            b.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            b.Property(x => x.RequestHash).IsRequired().HasMaxLength(128);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.ResultPayload).HasColumnType("jsonb");
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.ActorUserId, x.ProviderName, x.ProviderKey, x.IdempotencyKey })
                .IsUnique().HasFilter("\"TenantId\" IS NULL");
            b.HasIndex(x => new { x.TenantId, x.ActorUserId, x.ProviderName, x.ProviderKey, x.IdempotencyKey })
                .IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
            b.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        });

        builder.Entity<AccessReviewCampaign>(b =>
        {
            b.ToTable("AccessReviewCampaigns");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.Name).IsRequired().HasMaxLength(AccessReviewConsts.MaxCampaignNameLength);
            b.Property(x => x.SnapshotAtUtc).IsRequired();
            b.Property(x => x.DueAtUtc).IsRequired();
            b.Property(x => x.CreatedByUserId).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.ClosedAtUtc);
            b.Property(x => x.ClosedByUserId);
            b.HasIndex(x => new { x.TenantId, x.Status, x.DueAtUtc });
            b.HasIndex(x => x.TenantId)
                .IsUnique()
                .HasFilter($"\"Status\" = {(int)AccessReviewCampaignStatus.Active}")
                .HasDatabaseName("UX_AccessReviewCampaigns_OneActivePerTenant");
        });

        builder.Entity<AccessReviewItem>(b =>
        {
            b.ToTable("AccessReviewItems");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.CampaignId).IsRequired();
            b.Property(x => x.SourceGrantId).IsRequired();
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderKeyLength);
            b.Property(x => x.PermissionName).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.Effect).IsRequired();
            b.Property(x => x.SourceGrantCreationTimeUtc).IsRequired();
            b.Property(x => x.ExpiresAtUtc);
            b.Property(x => x.GrantReason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.TicketId).HasMaxLength(PermissionGrantConsts.MaxTicketIdLength);
            b.Property(x => x.GrantedByUserId);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.DecidedByUserId);
            b.Property(x => x.DecidedAtUtc);
            b.Property(x => x.DecisionReason).HasMaxLength(AccessReviewConsts.MaxDecisionReasonLength);
            b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.SourceGrantId });
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.ProviderName, x.ProviderKey });
            b.HasOne<AccessReviewCampaign>()
                .WithMany()
                .HasForeignKey(x => x.CampaignId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<IdentityPlatformRoleProjection>(b =>
        {
            b.ToTable("IdentityPlatformRoleProjections");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.LifecycleVersion).IsConcurrencyToken();
            b.Property(x => x.IsRetiring).IsRequired();
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.IsRetiring, x.UpdatedAtUtc });
        });

        builder.Entity<PermissionGrantChangeRequest>(b =>
        {
            b.ToTable("PermissionGrantChangeRequests", table =>
            {
                table.HasCheckConstraint(
                    "CK_PermissionGrantChangeRequests_ResourceScope",
                    "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"ExpectedResourceAssignmentVersion\" IS NULL AND \"ResourceAssignmentOperationId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"ExpectedResourceAssignmentVersion\" IS NOT NULL AND \"ResourceAssignmentOperationId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_PermissionGrantChangeRequests_Review",
                    "(\"ReviewState\" = 1 AND \"ReviewDueAtUtc\" IS NULL) OR (\"ReviewState\" IN (2, 3, 4, 5, 6) AND \"ReviewDueAtUtc\" IS NOT NULL)");
            });
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.PermissionName).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderKeyLength);
            b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
            b.Property(x => x.RequestedEffect).IsRequired();
            b.Property(x => x.ExpiresAtUtc);
            b.Property(x => x.Reason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.TicketId).HasMaxLength(PermissionGrantConsts.MaxTicketIdLength);
            b.Property(x => x.RequestedByUserId);
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.DecidedByUserId);
            b.Property(x => x.DecisionReason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.DecidedAtUtc);
            b.Property(x => x.GrantScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ResourceScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.OwnerServiceName).HasMaxLength(64);
            b.Property(x => x.ResourceName).HasMaxLength(PermissionGrantConsts.MaxResourceNameLength);
            b.Property(x => x.ResourceKey).HasMaxLength(PermissionGrantConsts.MaxResourceKeyLength);
            b.Property(x => x.OwnerRevision);
            b.Property(x => x.OwnerValidationReceiptId);
            b.Property(x => x.OwnerValidationReceiptRevision);
            b.Property(x => x.OwnerValidationReceiptExpiresAtUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.DelegationDefinitionVersionId);
            b.Property(x => x.ExpectedResourceAssignmentVersion);
            b.Property(x => x.ResourceAssignmentOperationId);
            b.Property(x => x.ReviewState).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ReviewDueAtUtc).HasColumnType("timestamp with time zone");

            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
            b.HasAlternateKey(x => new { x.ScopeTenantId, x.Id })
                .HasName("AK_PermissionGrantChangeRequests_ScopeTenantId_Id");
            b.HasIndex(x => new { x.TenantId, x.ResourceScopeKind, x.ResourceName, x.ResourceKey, x.Status, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.ReviewState, x.ReviewDueAtUtc });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptId, x.OwnerValidationReceiptRevision });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptExpiresAtUtc });
            b.HasIndex(x => new { x.ScopeTenantId, x.ResourceAssignmentOperationId }).IsUnique().HasFilter("\"ResourceAssignmentOperationId\" IS NOT NULL");
            b.HasOne<ResourcePermissionAssignmentOperation>().WithMany()
                .HasForeignKey(x => new { x.ScopeTenantId, x.ResourceAssignmentOperationId })
                .HasPrincipalKey(x => new { x.ScopeTenantId, x.Id }).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionGrantChangeRequests_ResourceAssignmentOperation");
            b.HasOne<PermissionDelegationDefinitionVersion>()
                .WithMany()
                .HasForeignKey(x => x.DelegationDefinitionVersionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionGrantChangeRequests_DelegationDefinitionVersion");
        });

        builder.Entity<PermissionApprovalDecisionOperation>(b =>
        {
            b.ToTable("PermissionApprovalDecisionOperations", table =>
            {
                table.HasCheckConstraint(
                    "CK_PermissionApprovalDecisionOperations_ResourceScope",
                    "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL)");
            });
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId);
            b.Property(x => x.ActorUserId).IsRequired();
            b.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            b.Property(x => x.RequestHash).IsRequired().HasMaxLength(64);
            b.Property(x => x.ResultJson).IsRequired().HasColumnType("text");
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.GrantScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ResourceScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.OwnerServiceName).HasMaxLength(64);
            b.Property(x => x.ResourceName).HasMaxLength(PermissionGrantConsts.MaxResourceNameLength);
            b.Property(x => x.ResourceKey).HasMaxLength(PermissionGrantConsts.MaxResourceKeyLength);
            b.Property(x => x.OwnerRevision);
            b.Property(x => x.OwnerValidationReceiptId);
            b.Property(x => x.OwnerValidationReceiptRevision);
            b.Property(x => x.OwnerValidationReceiptExpiresAtUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.DelegationDefinitionVersionId);
            b.HasIndex(x => new { x.ActorUserId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NULL")
                .HasDatabaseName("UX_PermissionApprovalDecisionOperations_Host_Idempotency");
            b.HasIndex(x => new { x.TenantId, x.ActorUserId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL")
                .HasDatabaseName("UX_PermissionApprovalDecisionOperations_Tenant_Idempotency");
            b.HasIndex(x => new { x.TenantId, x.ResourceScopeKind, x.ResourceName, x.ResourceKey, x.OwnerValidationReceiptId, x.OwnerValidationReceiptRevision });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptExpiresAtUtc });
            b.HasOne<PermissionDelegationDefinitionVersion>()
                .WithMany()
                .HasForeignKey(x => x.DelegationDefinitionVersionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionApprovalDecisionOperations_DelegationDefinitionVersion");
        });

        builder.Entity<PermissionGrantHistoryEntry>(b =>
        {
            b.ToTable("PermissionGrantHistoryEntries", table =>
            {
                table.HasCheckConstraint(
                    "CK_PermissionGrantHistoryEntries_ResourceScope",
                    "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"ApprovalRequestId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"ResourceOwnerRevision\" > 0 AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"OccurredAtUtc\")");
                table.HasCheckConstraint(
                    "CK_PermissionGrantHistoryEntries_Review",
                    "\"ReviewState\" IN (1, 2, 3, 4, 5, 6)");
                table.HasCheckConstraint(
                    "CK_PermissionGrantHistoryEntries_ApprovalScope",
                    "(\"ApprovalRequestId\" IS NULL AND \"ApprovalRequestScopeTenantId\" IS NULL) OR (\"ApprovalRequestId\" IS NOT NULL AND \"TenantId\" IS NOT NULL AND \"ApprovalRequestScopeTenantId\" = \"TenantId\")");
            });
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionsConsts.MaxProviderKeyLength);
            b.Property(x => x.PermissionName).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.ResourceName).HasMaxLength(PermissionGrantConsts.MaxResourceNameLength);
            b.Property(x => x.ResourceKey).HasMaxLength(PermissionGrantConsts.MaxResourceKeyLength);
            b.Property(x => x.ResourceOwnerRevision);
            b.Property(x => x.ChangeType).IsRequired();
            b.Property(x => x.OldEffect);
            b.Property(x => x.NewEffect);
            b.Property(x => x.ExpiresAtUtc);
            b.Property(x => x.Reason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.TicketId).HasMaxLength(PermissionGrantConsts.MaxTicketIdLength);
            b.Property(x => x.ActorUserId);
            b.Property(x => x.OccurredAtUtc).IsRequired();
            b.Property(x => x.GrantScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ResourceScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.OwnerServiceName).HasMaxLength(64);
            b.Property(x => x.DelegationDefinitionVersionId);
            b.Property(x => x.OwnerValidationReceiptId);
            b.Property(x => x.OwnerValidationReceiptRevision);
            b.Property(x => x.OwnerValidationReceiptExpiresAtUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.ApprovalRequestId);
            b.Property(x => x.ApprovalRequestScopeTenantId);
            b.Property(x => x.ReviewState).HasConversion<short>().HasColumnType("smallint").IsRequired();

            // Primary read path: history for one principal, newest first.
            b.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey, x.OccurredAtUtc });
            b.HasIndex(x => new { x.TenantId, x.PermissionName });
            b.HasIndex(x => new { x.TenantId, x.ResourceScopeKind, x.ResourceName, x.ResourceKey, x.OccurredAtUtc })
                .IsDescending(false, false, false, false, true);
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptId, x.OwnerValidationReceiptRevision });
            b.HasOne<PermissionDelegationDefinitionVersion>()
                .WithMany()
                .HasForeignKey(x => x.DelegationDefinitionVersionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionGrantHistoryEntries_DelegationDefinitionVersion");
            b.HasOne<PermissionGrantChangeRequest>()
                .WithMany()
                .HasForeignKey(x => new { x.ApprovalRequestScopeTenantId, x.ApprovalRequestId })
                .HasPrincipalKey(x => new { x.ScopeTenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionGrantHistoryEntries_ApprovalRequest");
        });

        builder.Entity<ResourcePermissionGrant>(b =>
        {
            b.ToTable("ResourcePermissionGrants");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.Name).IsRequired().HasMaxLength(PermissionDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.ResourceName).IsRequired().HasMaxLength(PermissionGrantConsts.MaxResourceNameLength);
            b.Property(x => x.ResourceKey).IsRequired().HasMaxLength(PermissionGrantConsts.MaxResourceKeyLength);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionGrantConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionGrantConsts.MaxProviderKeyLength);
            b.Property(x => x.OwnerRevision).IsRequired();
            b.Property(x => x.Effect).IsRequired();
            b.Property(x => x.CreationTimeUtc).IsRequired();
            b.Property(x => x.ExpiresAtUtc);
            b.Property(x => x.Reason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.TicketId).HasMaxLength(PermissionGrantConsts.MaxTicketIdLength);
            b.Property(x => x.GrantedByUserId);
            b.Property(x => x.GrantScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ResourceScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.OwnerServiceName).HasMaxLength(64);
            b.Property(x => x.DelegationDefinitionVersionId);
            b.Property(x => x.OwnerValidationReceiptId);
            b.Property(x => x.OwnerValidationReceiptRevision);
            b.Property(x => x.OwnerValidationReceiptExpiresAtUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.ApprovalState).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ReviewState).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.ReviewDueAtUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.LastReviewedAtUtc).HasColumnType("timestamp with time zone");

            b.ToTable(table =>
            {
                table.HasCheckConstraint("CK_ResourcePermissionGrants_OwnerRevision", "\"OwnerRevision\" > 0");
                table.HasCheckConstraint(
                    "CK_ResourcePermissionGrants_Expiry",
                    "\"ExpiresAtUtc\" IS NULL OR \"ExpiresAtUtc\" > \"CreationTimeUtc\"");
                table.HasCheckConstraint(
                    "CK_ResourcePermissionGrants_ResourceScope",
                    "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreationTimeUtc\" AND \"ApprovalState\" IN (1, 3))");
                table.HasCheckConstraint(
                    "CK_ResourcePermissionGrants_Review",
                    "(\"ReviewState\" = 1 AND \"ReviewDueAtUtc\" IS NULL) OR (\"ReviewState\" IN (2, 3, 4, 5, 6) AND \"ReviewDueAtUtc\" IS NOT NULL)");
            });

            b.HasIndex(x => new { x.TenantId, x.Name, x.ResourceName, x.ResourceKey, x.ProviderName, x.ProviderKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ResourceName, x.ResourceKey, x.ProviderName, x.ProviderKey });
            b.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey });
            b.HasIndex(x => x.ExpiresAtUtc);
            b.HasIndex(x => new { x.TenantId, x.ApprovalState, x.ReviewState, x.ReviewDueAtUtc });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptId, x.OwnerValidationReceiptRevision });
            b.HasIndex(x => new { x.TenantId, x.ApprovalState, x.ExpiresAtUtc });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptExpiresAtUtc });
            b.HasOne<PermissionDelegationDefinitionVersion>()
                .WithMany()
                .HasForeignKey(x => x.DelegationDefinitionVersionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_ResourcePermissionGrants_DelegationDefinitionVersion");
        });

        builder.Entity<ResourcePermissionAssignmentState>(b =>
        {
            b.ToTable("ResourcePermissionAssignmentStates", table =>
            {
                table.HasCheckConstraint("CK_ResourcePermissionAssignmentStates_Tenant", "\"TenantId\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_ResourcePermissionAssignmentStates_OwnerValidation",
                    "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"EffectiveScopeRevision\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"UpdatedAtUtc\" AND \"EffectiveScopeRevision\" > 0)");
            });
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.ResourceName).IsRequired().HasMaxLength(PermissionGrantConsts.MaxResourceNameLength);
            b.Property(x => x.ResourceKey).IsRequired().HasMaxLength(PermissionGrantConsts.MaxResourceKeyLength);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionGrantConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionGrantConsts.MaxProviderKeyLength);
            b.Property(x => x.Version).IsRequired().IsConcurrencyToken();
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.Property(x => x.ResourceScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.OwnerServiceName).HasMaxLength(64);
            b.Property(x => x.OwnerRevision);
            b.Property(x => x.OwnerValidationReceiptId);
            b.Property(x => x.OwnerValidationReceiptRevision);
            b.Property(x => x.OwnerValidationReceiptExpiresAtUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.EffectiveScopeRevision);
            b.HasIndex(x => new { x.TenantId, x.ResourceScopeKind, x.ResourceName, x.ResourceKey, x.ProviderName, x.ProviderKey })
                .IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ResourceScopeKind, x.ResourceName, x.ResourceKey });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptId, x.OwnerValidationReceiptRevision });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptExpiresAtUtc });
        });

        builder.Entity<ResourcePermissionAssignmentOperation>(b =>
        {
            b.ToTable("ResourcePermissionAssignmentOperations", table =>
            {
                table.HasCheckConstraint("CK_ResourcePermissionAssignmentOperations_Tenant", "\"TenantId\" IS NOT NULL AND \"ScopeTenantId\" = \"TenantId\"");
                table.HasCheckConstraint(
                    "CK_ResourcePermissionAssignmentOperations_OwnerValidation",
                    "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\")");
            });
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.ScopeTenantId).IsRequired();
            b.Property(x => x.ActorUserId).IsRequired();
            b.Property(x => x.ResourceName).IsRequired().HasMaxLength(PermissionGrantConsts.MaxResourceNameLength);
            b.Property(x => x.ResourceKey).IsRequired().HasMaxLength(PermissionGrantConsts.MaxResourceKeyLength);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(PermissionGrantConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(PermissionGrantConsts.MaxProviderKeyLength);
            b.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(ResourcePermissionAssignmentConsts.MaxIdempotencyKeyLength);
            b.Property(x => x.RequestHash).IsRequired().HasMaxLength(ResourcePermissionAssignmentConsts.MaxRequestHashLength);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.ResultPayload).HasColumnType("jsonb");
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.CompletedAtUtc);
            b.Property(x => x.ResourceScopeKind).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.OwnerServiceName).HasMaxLength(64);
            b.Property(x => x.OwnerRevision);
            b.Property(x => x.OwnerValidationReceiptId);
            b.Property(x => x.OwnerValidationReceiptRevision);
            b.Property(x => x.OwnerValidationReceiptExpiresAtUtc).HasColumnType("timestamp with time zone");
            b.HasIndex(x => new
            {
                x.TenantId,
                x.ActorUserId,
                x.ResourceScopeKind,
                x.ResourceName,
                x.ResourceKey,
                x.ProviderName,
                x.ProviderKey,
                x.IdempotencyKey
            }).IsUnique();
            b.HasAlternateKey(x => new { x.ScopeTenantId, x.Id }).HasName("AK_ResourcePermissionAssignmentOperations_ScopeTenantId_Id");
            b.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptId, x.OwnerValidationReceiptRevision });
            b.HasIndex(x => new { x.TenantId, x.OwnerValidationReceiptExpiresAtUtc });
        });

        builder.Entity<PermissionConflictRule>(b =>
        {
            b.ToTable("PermissionConflictRules");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.PermissionA).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.PermissionB).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.Reason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.CreationTimeUtc).IsRequired();
            b.Property(x => x.LastModifiedTimeUtc);
            b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);

            b.HasIndex(x => new { x.TenantId, x.PermissionA, x.PermissionB }).IsUnique();
        });

        builder.Entity<PermissionConflictRuleChangeRequest>(b =>
        {
            b.ToTable("PermissionConflictRuleChangeRequests");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.ChangeType).IsRequired();
            b.Property(x => x.TargetRuleId);
            b.Property(x => x.ExpectedRuleVersion);
            b.Property(x => x.PermissionA).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.PermissionB).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.RuleReason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.RequestReason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.TicketId).HasMaxLength(PermissionGrantConsts.MaxTicketIdLength);
            b.Property(x => x.RequestedByUserId);
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.DecidedByUserId);
            b.Property(x => x.DecisionReason).HasMaxLength(PermissionGrantConsts.MaxReasonLength);
            b.Property(x => x.DecidedAtUtc);
            b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);

            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
            // At most one pending change request may target a given rule at a time.
            b.HasIndex(x => x.TargetRuleId)
                .IsUnique()
                .HasFilter("\"Status\" = 1 AND \"TargetRuleId\" IS NOT NULL")
                .HasDatabaseName("UX_PermissionConflictRuleChangeRequests_PendingPerRule");
        });

        builder.Entity<Permissions.Domain.ImportExport.PermissionImportJob>(b =>
        {
            b.ToTable("PermissionImportJobs");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.Kind).IsRequired().HasMaxLength(64);
            b.Property(x => x.Format).IsRequired().HasMaxLength(16);
            b.Property(x => x.ProviderName).HasMaxLength(128);
            b.Property(x => x.ProviderKey).HasMaxLength(256);
            b.Property(x => x.ResourceName).HasMaxLength(128);
            b.Property(x => x.ResourceKey).HasMaxLength(256);
            b.Property(x => x.Content).IsRequired().HasColumnType("text");
            b.Property(x => x.ContentHash).IsRequired().HasMaxLength(128);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.RequestedByUserId);
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.StartedAtUtc);
            b.Property(x => x.CompletedAtUtc);
            b.Property(x => x.FailureReason).HasMaxLength(512);
            b.Property(x => x.ApplyTokenHash).HasMaxLength(128);
            b.Property(x => x.ApplyTokenExpiresAtUtc);
            b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);

            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
        });

        builder.Entity<PermissionNode>(b =>
        {
            b.ToTable("PermissionNodes");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.Name).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.DisplayName).HasMaxLength(PermissionsConsts.MaxDisplayNameLength);
            b.Property(x => x.Description).HasMaxLength(PermissionsConsts.MaxDescriptionLength);
            b.Property(x => x.GroupName).IsRequired().HasMaxLength(PermissionsConsts.MaxGroupNameLength);
            b.Property(x => x.ElementType).IsRequired();
            b.Property(x => x.ElementKey).IsRequired().HasMaxLength(PermissionsConsts.MaxElementKeyLength);
            b.Property(x => x.ParentName).HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.IsEnabled).IsRequired();
            b.Property(x => x.AllowsWildcard).IsRequired();
            b.Property(x => x.RequiresApproval).IsRequired();
            b.Property(x => x.RequiredFeatures).IsRequired();
            b.Property(x => x.AllowedProviders).IsRequired();
            b.Property(x => x.AllowedActions).IsRequired();
            b.Property(x => x.SortOrder).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);

            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ElementType, x.ElementKey });
        });

        builder.Entity<PermissionFeatureBinding>(b =>
        {
            b.ToTable("PermissionFeatureBindings");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.PermissionName).IsRequired().HasMaxLength(PermissionsConsts.MaxPermissionNameLength);
            b.Property(x => x.FeatureName).IsRequired().HasMaxLength(PermissionsConsts.MaxFeatureNameLength);
            b.Property(x => x.Requirement).IsRequired();

            b.HasIndex(x => new { x.TenantId, x.PermissionName, x.FeatureName }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.PermissionName });
        });

        builder.Entity<PermissionGroupDefinitionRecord>(b =>
        {
            b.ToTable("PermissionGroups");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(PermissionGroupDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(PermissionGroupDefinitionRecordConsts.MaxDisplayNameLength);

            b.HasIndex(x => new { x.Name }).IsUnique();
        });

        builder.Entity<PermissionDefinitionRecord>(b =>
        {
            b.ToTable("Permissions");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.GroupName).HasMaxLength(PermissionGroupDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.Name).IsRequired().HasMaxLength(PermissionDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.ResourceName).HasMaxLength(PermissionDefinitionRecordConsts.MaxResourceNameLength);
            b.Property(x => x.ManagementPermissionName).HasMaxLength(PermissionDefinitionRecordConsts.MaxManagementPermissionNameLength);
            b.Property(x => x.ParentName).HasMaxLength(PermissionDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(PermissionDefinitionRecordConsts.MaxDisplayNameLength);
            b.Property(x => x.Providers).HasMaxLength(PermissionDefinitionRecordConsts.MaxProvidersLength);
            b.Property(x => x.StateCheckers).HasMaxLength(PermissionDefinitionRecordConsts.MaxStateCheckersLength);

            b.HasIndex(x => new { x.ResourceName, x.Name }).IsUnique();
            b.HasIndex(x => new { x.GroupName });
        });

        builder.Entity<FeatureGroupDefinitionRecord>(b =>
        {
            b.ToTable("FeatureGroups");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(FeatureGroupDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(FeatureGroupDefinitionRecordConsts.MaxDisplayNameLength);

            b.HasIndex(x => new { x.Name }).IsUnique();
        });

        builder.Entity<FeatureDefinitionRecord>(b =>
        {
            b.ToTable("Features");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.GroupName).IsRequired().HasMaxLength(FeatureGroupDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.Name).IsRequired().HasMaxLength(FeatureDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.ParentName).HasMaxLength(FeatureDefinitionRecordConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(FeatureDefinitionRecordConsts.MaxDisplayNameLength);
            b.Property(x => x.Description).HasMaxLength(FeatureDefinitionRecordConsts.MaxDescriptionLength);
            b.Property(x => x.DefaultValue).HasMaxLength(FeatureDefinitionRecordConsts.MaxDefaultValueLength);
            b.Property(x => x.AllowedProviders).HasMaxLength(FeatureDefinitionRecordConsts.MaxAllowedProvidersLength);
            b.Property(x => x.ValueType).HasMaxLength(FeatureDefinitionRecordConsts.MaxValueTypeLength);
            b.Property(x => x.AllowedValues).HasMaxLength(FeatureDefinitionRecordConsts.MaxAllowedValuesLength);
            b.Property(x => x.Unit).HasMaxLength(FeatureDefinitionRecordConsts.MaxUnitLength);

            b.HasIndex(x => new { x.Name }).IsUnique();
            b.HasIndex(x => new { x.GroupName });
        });

        builder.Entity<FeatureValue>(b =>
        {
            b.ToTable("FeatureValues");
            b.HasKey(x => x.Id);

            b.ConfigureByConvention();

            b.Property(x => x.TenantId);
            b.Property(x => x.Name).IsRequired().HasMaxLength(FeatureValueConsts.MaxNameLength);
            b.Property(x => x.Value).IsRequired().HasMaxLength(FeatureValueConsts.MaxValueLength);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(FeatureValueConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).HasMaxLength(FeatureValueConsts.MaxProviderKeyLength);
            b.Property(x => x.ProvisioningSource).HasMaxLength(FeatureValueConsts.MaxProviderKeyLength);
            b.Property(x => x.ProvisioningSourceAggregateId);
            b.Property(x => x.ProvisioningSourceRevision);
            b.Property(x => x.ProvisioningEffectiveAtUtc);

            b.HasIndex(x => new { x.TenantId, x.Name, x.ProviderName, x.ProviderKey })
                .IsUnique()
                .AreNullsDistinct(false);
        });

        builder.Entity<TenantEntitlementProvisioningReceipt>(b =>
        {
            b.ToTable("TenantEntitlementProvisioningReceipts");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.SourceService).IsRequired().HasMaxLength(FeatureValueConsts.MaxProviderKeyLength);
            b.Property(x => x.SourceAggregateId).IsRequired();
            b.Property(x => x.SourceRevision).IsRequired();
            b.Property(x => x.EffectiveAtUtc).IsRequired();
            b.Property(x => x.ValuesChecksum).IsRequired().HasMaxLength(64);
            b.Property(x => x.ProviderStamp).IsRequired();
            b.Property(x => x.AppliedAtUtc).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.SourceService, x.SourceAggregateId, x.SourceRevision }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SourceService, x.EffectiveAtUtc, x.SourceRevision });
        });

        builder.Entity<FeatureProviderState>(b =>
        {
            b.ToTable("FeatureProviderStates");
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.TenantId);
            b.Property(x => x.ProviderName).IsRequired().HasMaxLength(FeatureValueConsts.MaxProviderNameLength);
            b.Property(x => x.ProviderKey).IsRequired().HasMaxLength(FeatureValueConsts.MaxProviderKeyLength);
            b.Property(x => x.Stamp).IsConcurrencyToken().HasDefaultValue(0L);
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey })
                .IsUnique()
                .AreNullsDistinct(false);
        });
    }

    private static void ConfigurePermissionDelegationDefinitions(ModelBuilder builder)
    {
        builder.Entity<PermissionDelegationDefinition>(b =>
        {
            b.ToTable("PermissionDelegationDefinitions", table =>
            {
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitions_Lifecycle",
                    "\"Lifecycle\" IN (1, 2)");
            });
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.PermissionName).IsRequired().HasMaxLength(256);
            b.Property(x => x.OwningService).IsRequired().HasMaxLength(64);
            b.Property(x => x.Lifecycle).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.CurrentVersionId);
            b.HasIndex(x => x.PermissionName)
                .IsUnique()
                .HasDatabaseName("UX_PermissionDelegationDefinitions_PermissionName");
            b.HasOne<PermissionDelegationDefinitionVersion>()
                .WithMany()
                .HasForeignKey(x => new { x.Id, x.CurrentVersionId })
                .HasPrincipalKey(x => new { x.DefinitionId, x.Id })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionDelegationDefinitions_CurrentVersion");
        });

        builder.Entity<PermissionDelegationDefinitionVersion>(b =>
        {
            b.ToTable("PermissionDelegationDefinitionVersions", table =>
            {
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitionVersions_Version",
                    "\"Version\" > 0");
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitionVersions_Flags",
                    "\"AllowedTargetKinds\" > 0 AND (\"AllowedTargetKinds\" & ~3) = 0 AND \"AllowedScopeKinds\" > 0 AND (\"AllowedScopeKinds\" & ~5) = 0 AND \"AllowedEffects\" > 0 AND (\"AllowedEffects\" & ~3) = 0");
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitionVersions_ExpiryAndReview",
                    "(\"MaximumExpirySeconds\" IS NULL OR \"MaximumExpirySeconds\" > 0) AND (\"ReviewCadenceDays\" IS NULL OR \"ReviewCadenceDays\" > 0)");
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitionVersions_Approval",
                    "(\"RequiresApproval\" AND \"ApproverPermissionName\" IS NOT NULL) OR (NOT \"RequiresApproval\" AND \"ApproverPermissionName\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitionVersions_EffectivePeriod",
                    "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                table.HasCheckConstraint(
                    "CK_PermissionDelegationDefinitionVersions_PublicationState",
                    "\"PublicationState\" IN (1, 2, 3)");
            });
            b.HasKey(x => x.Id);
            b.ConfigureByConvention();
            b.Property(x => x.DefinitionId).IsRequired();
            b.Property(x => x.Version).IsRequired();
            b.Property(x => x.PublicationState).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.SourceDefinitionChecksum).IsRequired().HasColumnType("char(64)");
            b.Property(x => x.ManagerPermissionName).IsRequired().HasMaxLength(256);
            b.Property(x => x.AllowedTargetKinds).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.AllowedScopeKinds).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.AllowedEffects).HasConversion<short>().HasColumnType("smallint").IsRequired();
            b.Property(x => x.MaximumExpirySeconds);
            b.Property(x => x.RequiresApproval).IsRequired();
            b.Property(x => x.ApproverPermissionName).HasMaxLength(256);
            b.Property(x => x.ReviewCadenceDays).HasColumnType("smallint");
            b.Property(x => x.RequiredFeatureName).HasMaxLength(256);
            b.Property(x => x.Delegable).IsRequired();
            b.Property(x => x.EffectiveFromUtc).HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.EffectiveToUtc).HasColumnType("timestamp with time zone");
            b.HasAlternateKey(x => new { x.DefinitionId, x.Id })
                .HasName("AK_PermissionDelegationDefinitionVersions_DefinitionId_Id");
            b.HasIndex(x => new { x.DefinitionId, x.Version })
                .IsUnique()
                .HasDatabaseName("UX_PermissionDelegationDefinitionVersions_DefinitionId_Version");
            b.HasIndex(x => x.SourceDefinitionChecksum)
                .IsUnique()
                .HasFilter("\"PublicationState\" = 2")
                .HasDatabaseName("UX_PermissionDelegationDefinitionVersions_PublishedChecksum");
            b.HasOne<PermissionDelegationDefinition>()
                .WithMany()
                .HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_PermissionDelegationDefinitionVersions_Definition");
        });
    }
}
