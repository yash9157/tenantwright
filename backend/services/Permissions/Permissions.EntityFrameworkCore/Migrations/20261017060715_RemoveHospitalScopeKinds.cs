using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Permissions.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHospitalScopeKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourcePermissionGrants_ResourceScope",
                table: "ResourcePermissionGrants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourcePermissionAssignmentStates_OwnerValidation",
                table: "ResourcePermissionAssignmentStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourcePermissionAssignmentOperations_OwnerValidation",
                table: "ResourcePermissionAssignmentOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionGrantHistoryEntries_ResourceScope",
                table: "PermissionGrantHistoryEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionGrantChangeRequests_ResourceScope",
                table: "PermissionGrantChangeRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionDelegationDefinitionVersions_Flags",
                table: "PermissionDelegationDefinitionVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionApprovalDecisionOperations_ResourceScope",
                table: "PermissionApprovalDecisionOperations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourcePermissionGrants_ResourceScope",
                table: "ResourcePermissionGrants",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreationTimeUtc\" AND \"ApprovalState\" IN (1, 3))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourcePermissionAssignmentStates_OwnerValidation",
                table: "ResourcePermissionAssignmentStates",
                sql: "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"EffectiveScopeRevision\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"UpdatedAtUtc\" AND \"EffectiveScopeRevision\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourcePermissionAssignmentOperations_OwnerValidation",
                table: "ResourcePermissionAssignmentOperations",
                sql: "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionGrantHistoryEntries_ResourceScope",
                table: "PermissionGrantHistoryEntries",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"ApprovalRequestId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"ResourceOwnerRevision\" > 0 AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"OccurredAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionGrantChangeRequests_ResourceScope",
                table: "PermissionGrantChangeRequests",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"ExpectedResourceAssignmentVersion\" IS NULL AND \"ResourceAssignmentOperationId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"ExpectedResourceAssignmentVersion\" IS NOT NULL AND \"ResourceAssignmentOperationId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionDelegationDefinitionVersions_Flags",
                table: "PermissionDelegationDefinitionVersions",
                sql: "\"AllowedTargetKinds\" > 0 AND (\"AllowedTargetKinds\" & ~3) = 0 AND \"AllowedScopeKinds\" > 0 AND (\"AllowedScopeKinds\" & ~5) = 0 AND \"AllowedEffects\" > 0 AND (\"AllowedEffects\" & ~3) = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionApprovalDecisionOperations_ResourceScope",
                table: "PermissionApprovalDecisionOperations",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~5) = 0 AND (\"ResourceScopeKind\" & 4) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourcePermissionGrants_ResourceScope",
                table: "ResourcePermissionGrants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourcePermissionAssignmentStates_OwnerValidation",
                table: "ResourcePermissionAssignmentStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourcePermissionAssignmentOperations_OwnerValidation",
                table: "ResourcePermissionAssignmentOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionGrantHistoryEntries_ResourceScope",
                table: "PermissionGrantHistoryEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionGrantChangeRequests_ResourceScope",
                table: "PermissionGrantChangeRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionDelegationDefinitionVersions_Flags",
                table: "PermissionDelegationDefinitionVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PermissionApprovalDecisionOperations_ResourceScope",
                table: "PermissionApprovalDecisionOperations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourcePermissionGrants_ResourceScope",
                table: "ResourcePermissionGrants",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreationTimeUtc\" AND \"ApprovalState\" IN (1, 3))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourcePermissionAssignmentStates_OwnerValidation",
                table: "ResourcePermissionAssignmentStates",
                sql: "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"EffectiveScopeRevision\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"UpdatedAtUtc\" AND \"EffectiveScopeRevision\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourcePermissionAssignmentOperations_OwnerValidation",
                table: "ResourcePermissionAssignmentOperations",
                sql: "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionGrantHistoryEntries_ResourceScope",
                table: "PermissionGrantHistoryEntries",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"ApprovalRequestId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"ResourceOwnerRevision\" > 0 AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"OccurredAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionGrantChangeRequests_ResourceScope",
                table: "PermissionGrantChangeRequests",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"ExpectedResourceAssignmentVersion\" IS NULL AND \"ResourceAssignmentOperationId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"ExpectedResourceAssignmentVersion\" IS NOT NULL AND \"ResourceAssignmentOperationId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionDelegationDefinitionVersions_Flags",
                table: "PermissionDelegationDefinitionVersions",
                sql: "\"AllowedTargetKinds\" > 0 AND (\"AllowedTargetKinds\" & ~3) = 0 AND \"AllowedScopeKinds\" > 0 AND (\"AllowedScopeKinds\" & ~15) = 0 AND \"AllowedEffects\" > 0 AND (\"AllowedEffects\" & ~3) = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermissionApprovalDecisionOperations_ResourceScope",
                table: "PermissionApprovalDecisionOperations",
                sql: "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL)");
        }
    }
}
