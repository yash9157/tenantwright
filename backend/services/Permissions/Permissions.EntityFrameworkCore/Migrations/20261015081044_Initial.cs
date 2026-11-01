using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Permissions.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "AccessReviewCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SnapshotAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviewCampaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureProviderStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Stamp = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureProviderStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Features",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ParentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DefaultValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsVisibleToClients = table.Column<bool>(type: "boolean", nullable: false),
                    IsAvailableToHost = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedProviders = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ValueType = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    MutationPolicy = table.Column<int>(type: "integer", nullable: false),
                    UsesAggregateTenantPool = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedValues = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    MinValue = table.Column<decimal>(type: "numeric", nullable: true),
                    MaxValue = table.Column<decimal>(type: "numeric", nullable: true),
                    Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Features", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProvisioningSource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProvisioningSourceAggregateId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProvisioningSourceRevision = table.Column<long>(type: "bigint", nullable: true),
                    ProvisioningEffectiveAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureValues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityPlatformRoleProjections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LifecycleVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsRetiring = table.Column<bool>(type: "boolean", nullable: false),
                    RetirementRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityPlatformRoleProjections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxState",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxState", x => x.Id);
                    table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
                });

            migrationBuilder.CreateTable(
                name: "MessagingReplayState",
                schema: "public",
                columns: table => new
                {
                    ReplayId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Destination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AuditId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingReplayState", x => x.ReplayId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxState",
                schema: "public",
                columns: table => new
                {
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true),
                    BusName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
                });

            migrationBuilder.CreateTable(
                name: "PermissionAssignmentOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResultPayload = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionAssignmentOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionAssignmentStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionAssignmentStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionConflictRuleChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    TargetRuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedRuleVersion = table.Column<long>(type: "bigint", nullable: true),
                    PermissionA = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PermissionB = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RuleReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RequestReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionConflictRuleChangeRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionConflictRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    PermissionA = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PermissionB = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreationTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionConflictRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionFeatureBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    PermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Requirement = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionFeatureBindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Effect = table.Column<byte>(type: "smallint", nullable: false),
                    CreationTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionImportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProviderKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResourceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false),
                    UpsertCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedCount = table.Column<int>(type: "integer", nullable: false),
                    RemovedCount = table.Column<int>(type: "integer", nullable: false),
                    UnchangedCount = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ApplyTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ApplyTokenExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionImportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    GroupName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ElementType = table.Column<byte>(type: "smallint", nullable: false),
                    ElementKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ParentName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AllowsWildcard = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    RequiredFeatures = table.Column<string[]>(type: "text[]", nullable: false),
                    AllowedProviders = table.Column<string[]>(type: "text[]", nullable: false),
                    AllowedActions = table.Column<string[]>(type: "text[]", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ManagementPermissionName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ParentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MultiTenancySide = table.Column<byte>(type: "smallint", nullable: false),
                    Providers = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StateCheckers = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourcePermissionAssignmentOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResultPayload = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResourceScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OwnerRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePermissionAssignmentOperations", x => x.Id);
                    table.UniqueConstraint("AK_ResourcePermissionAssignmentOperations_ScopeTenantId_Id", x => new { x.ScopeTenantId, x.Id });
                    table.CheckConstraint("CK_ResourcePermissionAssignmentOperations_OwnerValidation", "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\")");
                    table.CheckConstraint("CK_ResourcePermissionAssignmentOperations_Tenant", "\"TenantId\" IS NOT NULL AND \"ScopeTenantId\" = \"TenantId\"");
                });

            migrationBuilder.CreateTable(
                name: "ResourcePermissionAssignmentStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResourceScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OwnerRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EffectiveScopeRevision = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePermissionAssignmentStates", x => x.Id);
                    table.CheckConstraint("CK_ResourcePermissionAssignmentStates_OwnerValidation", "(\"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"EffectiveScopeRevision\" IS NULL) OR (\"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"UpdatedAtUtc\" AND \"EffectiveScopeRevision\" > 0)");
                    table.CheckConstraint("CK_ResourcePermissionAssignmentStates_Tenant", "\"TenantId\" IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "TenantEntitlementProvisioningReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceService = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceAggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValuesChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderStamp = table.Column<long>(type: "bigint", nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantEntitlementProvisioningReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessReviewItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceGrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Effect = table.Column<byte>(type: "smallint", nullable: false),
                    SourceGrantCreationTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviewItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessReviewItems_AccessReviewCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "AccessReviewCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "public",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Headers = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    InboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    InboxConsumerId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MessageType = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DestinationAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResponseAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FaultAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                    table.ForeignKey(
                        name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
                        columns: x => new { x.InboxMessageId, x.InboxConsumerId },
                        principalSchema: "public",
                        principalTable: "InboxState",
                        principalColumns: new[] { "MessageId", "ConsumerId" });
                    table.ForeignKey(
                        name: "FK_OutboxMessage_OutboxState_OutboxId",
                        column: x => x.OutboxId,
                        principalSchema: "public",
                        principalTable: "OutboxState",
                        principalColumn: "OutboxId");
                });

            migrationBuilder.CreateTable(
                name: "PermissionApprovalDecisionOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GrantScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    ResourceScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OwnerRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DelegationDefinitionVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionApprovalDecisionOperations", x => x.Id);
                    table.CheckConstraint("CK_PermissionApprovalDecisionOperations_ResourceScope", "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "PermissionDelegationDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwningService = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Lifecycle = table.Column<short>(type: "smallint", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionDelegationDefinitions", x => x.Id);
                    table.CheckConstraint("CK_PermissionDelegationDefinitions_Lifecycle", "\"Lifecycle\" IN (1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "PermissionDelegationDefinitionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    PublicationState = table.Column<short>(type: "smallint", nullable: false),
                    SourceDefinitionChecksum = table.Column<string>(type: "char(64)", nullable: false),
                    ManagerPermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AllowedTargetKinds = table.Column<short>(type: "smallint", nullable: false),
                    AllowedScopeKinds = table.Column<short>(type: "smallint", nullable: false),
                    AllowedEffects = table.Column<short>(type: "smallint", nullable: false),
                    MaximumExpirySeconds = table.Column<int>(type: "integer", nullable: true),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    ApproverPermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewCadenceDays = table.Column<short>(type: "smallint", nullable: true),
                    RequiredFeatureName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Delegable = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionDelegationDefinitionVersions", x => x.Id);
                    table.UniqueConstraint("AK_PermissionDelegationDefinitionVersions_DefinitionId_Id", x => new { x.DefinitionId, x.Id });
                    table.CheckConstraint("CK_PermissionDelegationDefinitionVersions_Approval", "(\"RequiresApproval\" AND \"ApproverPermissionName\" IS NOT NULL) OR (NOT \"RequiresApproval\" AND \"ApproverPermissionName\" IS NULL)");
                    table.CheckConstraint("CK_PermissionDelegationDefinitionVersions_EffectivePeriod", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                    table.CheckConstraint("CK_PermissionDelegationDefinitionVersions_ExpiryAndReview", "(\"MaximumExpirySeconds\" IS NULL OR \"MaximumExpirySeconds\" > 0) AND (\"ReviewCadenceDays\" IS NULL OR \"ReviewCadenceDays\" > 0)");
                    table.CheckConstraint("CK_PermissionDelegationDefinitionVersions_Flags", "\"AllowedTargetKinds\" > 0 AND (\"AllowedTargetKinds\" & ~3) = 0 AND \"AllowedScopeKinds\" > 0 AND (\"AllowedScopeKinds\" & ~15) = 0 AND \"AllowedEffects\" > 0 AND (\"AllowedEffects\" & ~3) = 0");
                    table.CheckConstraint("CK_PermissionDelegationDefinitionVersions_PublicationState", "\"PublicationState\" IN (1, 2, 3)");
                    table.CheckConstraint("CK_PermissionDelegationDefinitionVersions_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_PermissionDelegationDefinitionVersions_Definition",
                        column: x => x.DefinitionId,
                        principalTable: "PermissionDelegationDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermissionGrantChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedEffect = table.Column<byte>(type: "smallint", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    GrantScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    ResourceScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OwnerRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DelegationDefinitionVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedResourceAssignmentVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceAssignmentOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewState = table.Column<short>(type: "smallint", nullable: false),
                    ReviewDueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGrantChangeRequests", x => x.Id);
                    table.UniqueConstraint("AK_PermissionGrantChangeRequests_ScopeTenantId_Id", x => new { x.ScopeTenantId, x.Id });
                    table.CheckConstraint("CK_PermissionGrantChangeRequests_ResourceScope", "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"ResourceName\" IS NULL AND \"ResourceKey\" IS NULL AND \"OwnerRevision\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"ExpectedResourceAssignmentVersion\" IS NULL AND \"ResourceAssignmentOperationId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"OwnerRevision\" > 0 AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreatedAtUtc\" AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"ExpectedResourceAssignmentVersion\" IS NOT NULL AND \"ResourceAssignmentOperationId\" IS NOT NULL)");
                    table.CheckConstraint("CK_PermissionGrantChangeRequests_Review", "(\"ReviewState\" = 1 AND \"ReviewDueAtUtc\" IS NULL) OR (\"ReviewState\" IN (2, 3, 4, 5, 6) AND \"ReviewDueAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PermissionGrantChangeRequests_DelegationDefinitionVersion",
                        column: x => x.DelegationDefinitionVersionId,
                        principalTable: "PermissionDelegationDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermissionGrantChangeRequests_ResourceAssignmentOperation",
                        columns: x => new { x.ScopeTenantId, x.ResourceAssignmentOperationId },
                        principalTable: "ResourcePermissionAssignmentOperations",
                        principalColumns: new[] { "ScopeTenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResourcePermissionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwnerRevision = table.Column<long>(type: "bigint", nullable: false),
                    Effect = table.Column<byte>(type: "smallint", nullable: false),
                    CreationTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    ResourceScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DelegationDefinitionVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovalState = table.Column<short>(type: "smallint", nullable: false),
                    ReviewState = table.Column<short>(type: "smallint", nullable: false),
                    ReviewDueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePermissionGrants", x => x.Id);
                    table.CheckConstraint("CK_ResourcePermissionGrants_Expiry", "\"ExpiresAtUtc\" IS NULL OR \"ExpiresAtUtc\" > \"CreationTimeUtc\"");
                    table.CheckConstraint("CK_ResourcePermissionGrants_OwnerRevision", "\"OwnerRevision\" > 0");
                    table.CheckConstraint("CK_ResourcePermissionGrants_ResourceScope", "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"CreationTimeUtc\" AND \"ApprovalState\" IN (1, 3))");
                    table.CheckConstraint("CK_ResourcePermissionGrants_Review", "(\"ReviewState\" = 1 AND \"ReviewDueAtUtc\" IS NULL) OR (\"ReviewState\" IN (2, 3, 4, 5, 6) AND \"ReviewDueAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ResourcePermissionGrants_DelegationDefinitionVersion",
                        column: x => x.DelegationDefinitionVersionId,
                        principalTable: "PermissionDelegationDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermissionGrantHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResourceOwnerRevision = table.Column<long>(type: "bigint", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    OldEffect = table.Column<byte>(type: "smallint", nullable: true),
                    NewEffect = table.Column<byte>(type: "smallint", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GrantScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    ResourceScopeKind = table.Column<short>(type: "smallint", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DelegationDefinitionVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerValidationReceiptRevision = table.Column<long>(type: "bigint", nullable: true),
                    OwnerValidationReceiptExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovalRequestScopeTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGrantHistoryEntries", x => x.Id);
                    table.CheckConstraint("CK_PermissionGrantHistoryEntries_ApprovalScope", "(\"ApprovalRequestId\" IS NULL AND \"ApprovalRequestScopeTenantId\" IS NULL) OR (\"ApprovalRequestId\" IS NOT NULL AND \"TenantId\" IS NOT NULL AND \"ApprovalRequestScopeTenantId\" = \"TenantId\")");
                    table.CheckConstraint("CK_PermissionGrantHistoryEntries_ResourceScope", "(\"GrantScopeKind\" = 1 AND \"ResourceScopeKind\" = 0 AND \"OwnerServiceName\" IS NULL AND \"DelegationDefinitionVersionId\" IS NULL AND \"OwnerValidationReceiptId\" IS NULL AND \"OwnerValidationReceiptRevision\" IS NULL AND \"OwnerValidationReceiptExpiresAtUtc\" IS NULL AND \"ApprovalRequestId\" IS NULL) OR (\"GrantScopeKind\" = 2 AND \"TenantId\" IS NOT NULL AND \"ResourceScopeKind\" > 0 AND (\"ResourceScopeKind\" & ~15) = 0 AND (\"ResourceScopeKind\" & 12) <> 0 AND \"OwnerServiceName\" IS NOT NULL AND \"ResourceName\" IS NOT NULL AND \"ResourceKey\" IS NOT NULL AND \"ResourceOwnerRevision\" > 0 AND \"DelegationDefinitionVersionId\" IS NOT NULL AND \"OwnerValidationReceiptId\" IS NOT NULL AND \"OwnerValidationReceiptRevision\" > 0 AND \"OwnerValidationReceiptExpiresAtUtc\" > \"OccurredAtUtc\")");
                    table.CheckConstraint("CK_PermissionGrantHistoryEntries_Review", "\"ReviewState\" IN (1, 2, 3, 4, 5, 6)");
                    table.ForeignKey(
                        name: "FK_PermissionGrantHistoryEntries_ApprovalRequest",
                        columns: x => new { x.ApprovalRequestScopeTenantId, x.ApprovalRequestId },
                        principalTable: "PermissionGrantChangeRequests",
                        principalColumns: new[] { "ScopeTenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermissionGrantHistoryEntries_DelegationDefinitionVersion",
                        column: x => x.DelegationDefinitionVersionId,
                        principalTable: "PermissionDelegationDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewCampaigns_TenantId_Status_DueAtUtc",
                table: "AccessReviewCampaigns",
                columns: new[] { "TenantId", "Status", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_AccessReviewCampaigns_OneActivePerTenant",
                table: "AccessReviewCampaigns",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_CampaignId",
                table: "AccessReviewItems",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_TenantId_CampaignId_ProviderName_Provider~",
                table: "AccessReviewItems",
                columns: new[] { "TenantId", "CampaignId", "ProviderName", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_TenantId_CampaignId_Status",
                table: "AccessReviewItems",
                columns: new[] { "TenantId", "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_TenantId_SourceGrantId",
                table: "AccessReviewItems",
                columns: new[] { "TenantId", "SourceGrantId" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureGroups_Name",
                table: "FeatureGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureProviderStates_TenantId_ProviderName_ProviderKey",
                table: "FeatureProviderStates",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Features_GroupName",
                table: "Features",
                column: "GroupName");

            migrationBuilder.CreateIndex(
                name: "IX_Features_Name",
                table: "Features",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureValues_TenantId_Name_ProviderName_ProviderKey",
                table: "FeatureValues",
                columns: new[] { "TenantId", "Name", "ProviderName", "ProviderKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityPlatformRoleProjections_IsRetiring_UpdatedAtUtc",
                table: "IdentityPlatformRoleProjections",
                columns: new[] { "IsRetiring", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxState_Delivered",
                schema: "public",
                table: "InboxState",
                column: "Delivered");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingReplayState_ExpiresAt",
                schema: "public",
                table: "MessagingReplayState",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
                schema: "public",
                table: "OutboxMessage",
                columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_OutboxId_SequenceNumber",
                schema: "public",
                table: "OutboxMessage",
                columns: new[] { "OutboxId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxState_BusName_Created",
                schema: "public",
                table: "OutboxState",
                columns: new[] { "BusName", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionApprovalDecisionOperations_DelegationDefinitionVe~",
                table: "PermissionApprovalDecisionOperations",
                column: "DelegationDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionApprovalDecisionOperations_TenantId_OwnerValidati~",
                table: "PermissionApprovalDecisionOperations",
                columns: new[] { "TenantId", "OwnerValidationReceiptExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionApprovalDecisionOperations_TenantId_ResourceScope~",
                table: "PermissionApprovalDecisionOperations",
                columns: new[] { "TenantId", "ResourceScopeKind", "ResourceName", "ResourceKey", "OwnerValidationReceiptId", "OwnerValidationReceiptRevision" });

            migrationBuilder.CreateIndex(
                name: "UX_PermissionApprovalDecisionOperations_Host_Idempotency",
                table: "PermissionApprovalDecisionOperations",
                columns: new[] { "ActorUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_PermissionApprovalDecisionOperations_Tenant_Idempotency",
                table: "PermissionApprovalDecisionOperations",
                columns: new[] { "TenantId", "ActorUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAssignmentOperations_ActorUserId_ProviderName_Pro~",
                table: "PermissionAssignmentOperations",
                columns: new[] { "ActorUserId", "ProviderName", "ProviderKey", "IdempotencyKey" },
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAssignmentOperations_Status_CreatedAtUtc",
                table: "PermissionAssignmentOperations",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAssignmentOperations_TenantId_ActorUserId_Provide~",
                table: "PermissionAssignmentOperations",
                columns: new[] { "TenantId", "ActorUserId", "ProviderName", "ProviderKey", "IdempotencyKey" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAssignmentStates_ProviderName_ProviderKey",
                table: "PermissionAssignmentStates",
                columns: new[] { "ProviderName", "ProviderKey" },
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAssignmentStates_TenantId_ProviderName_ProviderKey",
                table: "PermissionAssignmentStates",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionConflictRuleChangeRequests_TenantId_Status_Create~",
                table: "PermissionConflictRuleChangeRequests",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_PermissionConflictRuleChangeRequests_PendingPerRule",
                table: "PermissionConflictRuleChangeRequests",
                column: "TargetRuleId",
                unique: true,
                filter: "\"Status\" = 1 AND \"TargetRuleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionConflictRules_TenantId_PermissionA_PermissionB",
                table: "PermissionConflictRules",
                columns: new[] { "TenantId", "PermissionA", "PermissionB" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionDelegationDefinitions_Id_CurrentVersionId",
                table: "PermissionDelegationDefinitions",
                columns: new[] { "Id", "CurrentVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_PermissionDelegationDefinitions_PermissionName",
                table: "PermissionDelegationDefinitions",
                column: "PermissionName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PermissionDelegationDefinitionVersions_DefinitionId_Version",
                table: "PermissionDelegationDefinitionVersions",
                columns: new[] { "DefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PermissionDelegationDefinitionVersions_PublishedChecksum",
                table: "PermissionDelegationDefinitionVersions",
                column: "SourceDefinitionChecksum",
                unique: true,
                filter: "\"PublicationState\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionFeatureBindings_TenantId_PermissionName",
                table: "PermissionFeatureBindings",
                columns: new[] { "TenantId", "PermissionName" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionFeatureBindings_TenantId_PermissionName_FeatureNa~",
                table: "PermissionFeatureBindings",
                columns: new[] { "TenantId", "PermissionName", "FeatureName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_DelegationDefinitionVersionId",
                table: "PermissionGrantChangeRequests",
                column: "DelegationDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_ScopeTenantId_ResourceAssignm~",
                table: "PermissionGrantChangeRequests",
                columns: new[] { "ScopeTenantId", "ResourceAssignmentOperationId" },
                unique: true,
                filter: "\"ResourceAssignmentOperationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_TenantId_OwnerValidationRece~1",
                table: "PermissionGrantChangeRequests",
                columns: new[] { "TenantId", "OwnerValidationReceiptId", "OwnerValidationReceiptRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_TenantId_OwnerValidationRecei~",
                table: "PermissionGrantChangeRequests",
                columns: new[] { "TenantId", "OwnerValidationReceiptExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_TenantId_ResourceScopeKind_Re~",
                table: "PermissionGrantChangeRequests",
                columns: new[] { "TenantId", "ResourceScopeKind", "ResourceName", "ResourceKey", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_TenantId_ReviewState_ReviewDu~",
                table: "PermissionGrantChangeRequests",
                columns: new[] { "TenantId", "ReviewState", "ReviewDueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantChangeRequests_TenantId_Status_CreatedAtUtc",
                table: "PermissionGrantChangeRequests",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantHistoryEntries_ApprovalRequestScopeTenantId_~",
                table: "PermissionGrantHistoryEntries",
                columns: new[] { "ApprovalRequestScopeTenantId", "ApprovalRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantHistoryEntries_DelegationDefinitionVersionId",
                table: "PermissionGrantHistoryEntries",
                column: "DelegationDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantHistoryEntries_TenantId_OwnerValidationRecei~",
                table: "PermissionGrantHistoryEntries",
                columns: new[] { "TenantId", "OwnerValidationReceiptId", "OwnerValidationReceiptRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantHistoryEntries_TenantId_PermissionName",
                table: "PermissionGrantHistoryEntries",
                columns: new[] { "TenantId", "PermissionName" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantHistoryEntries_TenantId_ProviderName_Provide~",
                table: "PermissionGrantHistoryEntries",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantHistoryEntries_TenantId_ResourceScopeKind_Re~",
                table: "PermissionGrantHistoryEntries",
                columns: new[] { "TenantId", "ResourceScopeKind", "ResourceName", "ResourceKey", "OccurredAtUtc" },
                descending: new[] { false, false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_ExpiresAtUtc",
                table: "PermissionGrants",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_ProviderName_ProviderKey_Name",
                table: "PermissionGrants",
                columns: new[] { "ProviderName", "ProviderKey", "Name" },
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_TenantId_Name",
                table: "PermissionGrants",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_TenantId_ProviderName_ProviderKey",
                table: "PermissionGrants",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_TenantId_ProviderName_ProviderKey_Name",
                table: "PermissionGrants",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey", "Name" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGroups_Name",
                table: "PermissionGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionImportJobs_TenantId_Status_CreatedAtUtc",
                table: "PermissionImportJobs",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionNodes_TenantId_ElementType_ElementKey",
                table: "PermissionNodes",
                columns: new[] { "TenantId", "ElementType", "ElementKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionNodes_TenantId_Name",
                table: "PermissionNodes",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_GroupName",
                table: "Permissions",
                column: "GroupName");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_ResourceName_Name",
                table: "Permissions",
                columns: new[] { "ResourceName", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentOperations_Status_CreatedAtUtc",
                table: "ResourcePermissionAssignmentOperations",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentOperations_TenantId_ActorUserId~",
                table: "ResourcePermissionAssignmentOperations",
                columns: new[] { "TenantId", "ActorUserId", "ResourceScopeKind", "ResourceName", "ResourceKey", "ProviderName", "ProviderKey", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentOperations_TenantId_OwnerValid~1",
                table: "ResourcePermissionAssignmentOperations",
                columns: new[] { "TenantId", "OwnerValidationReceiptId", "OwnerValidationReceiptRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentOperations_TenantId_OwnerValida~",
                table: "ResourcePermissionAssignmentOperations",
                columns: new[] { "TenantId", "OwnerValidationReceiptExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentStates_TenantId_OwnerValidatio~1",
                table: "ResourcePermissionAssignmentStates",
                columns: new[] { "TenantId", "OwnerValidationReceiptId", "OwnerValidationReceiptRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentStates_TenantId_OwnerValidation~",
                table: "ResourcePermissionAssignmentStates",
                columns: new[] { "TenantId", "OwnerValidationReceiptExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentStates_TenantId_ResourceScopeK~1",
                table: "ResourcePermissionAssignmentStates",
                columns: new[] { "TenantId", "ResourceScopeKind", "ResourceName", "ResourceKey", "ProviderName", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionAssignmentStates_TenantId_ResourceScopeKi~",
                table: "ResourcePermissionAssignmentStates",
                columns: new[] { "TenantId", "ResourceScopeKind", "ResourceName", "ResourceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_DelegationDefinitionVersionId",
                table: "ResourcePermissionGrants",
                column: "DelegationDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_ExpiresAtUtc",
                table: "ResourcePermissionGrants",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_ApprovalState_ExpiresAtUtc",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "ApprovalState", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_ApprovalState_ReviewState~",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "ApprovalState", "ReviewState", "ReviewDueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_Name_ResourceName_Resourc~",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "Name", "ResourceName", "ResourceKey", "ProviderName", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_OwnerValidationReceiptExp~",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "OwnerValidationReceiptExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_OwnerValidationReceiptId_~",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "OwnerValidationReceiptId", "OwnerValidationReceiptRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_ProviderName_ProviderKey",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePermissionGrants_TenantId_ResourceName_ResourceKey_~",
                table: "ResourcePermissionGrants",
                columns: new[] { "TenantId", "ResourceName", "ResourceKey", "ProviderName", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlementProvisioningReceipts_TenantId_SourceServi~1",
                table: "TenantEntitlementProvisioningReceipts",
                columns: new[] { "TenantId", "SourceService", "SourceAggregateId", "SourceRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlementProvisioningReceipts_TenantId_SourceServic~",
                table: "TenantEntitlementProvisioningReceipts",
                columns: new[] { "TenantId", "SourceService", "EffectiveAtUtc", "SourceRevision" });

            migrationBuilder.AddForeignKey(
                name: "FK_PermissionApprovalDecisionOperations_DelegationDefinitionVersion",
                table: "PermissionApprovalDecisionOperations",
                column: "DelegationDefinitionVersionId",
                principalTable: "PermissionDelegationDefinitionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PermissionDelegationDefinitions_CurrentVersion",
                table: "PermissionDelegationDefinitions",
                columns: new[] { "Id", "CurrentVersionId" },
                principalTable: "PermissionDelegationDefinitionVersions",
                principalColumns: new[] { "DefinitionId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PermissionDelegationDefinitions_CurrentVersion",
                table: "PermissionDelegationDefinitions");

            migrationBuilder.DropTable(
                name: "AccessReviewItems");

            migrationBuilder.DropTable(
                name: "FeatureGroups");

            migrationBuilder.DropTable(
                name: "FeatureProviderStates");

            migrationBuilder.DropTable(
                name: "Features");

            migrationBuilder.DropTable(
                name: "FeatureValues");

            migrationBuilder.DropTable(
                name: "IdentityPlatformRoleProjections");

            migrationBuilder.DropTable(
                name: "MessagingReplayState",
                schema: "public");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "public");

            migrationBuilder.DropTable(
                name: "PermissionApprovalDecisionOperations");

            migrationBuilder.DropTable(
                name: "PermissionAssignmentOperations");

            migrationBuilder.DropTable(
                name: "PermissionAssignmentStates");

            migrationBuilder.DropTable(
                name: "PermissionConflictRuleChangeRequests");

            migrationBuilder.DropTable(
                name: "PermissionConflictRules");

            migrationBuilder.DropTable(
                name: "PermissionFeatureBindings");

            migrationBuilder.DropTable(
                name: "PermissionGrantHistoryEntries");

            migrationBuilder.DropTable(
                name: "PermissionGrants");

            migrationBuilder.DropTable(
                name: "PermissionGroups");

            migrationBuilder.DropTable(
                name: "PermissionImportJobs");

            migrationBuilder.DropTable(
                name: "PermissionNodes");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "ResourcePermissionAssignmentStates");

            migrationBuilder.DropTable(
                name: "ResourcePermissionGrants");

            migrationBuilder.DropTable(
                name: "TenantEntitlementProvisioningReceipts");

            migrationBuilder.DropTable(
                name: "AccessReviewCampaigns");

            migrationBuilder.DropTable(
                name: "InboxState",
                schema: "public");

            migrationBuilder.DropTable(
                name: "OutboxState",
                schema: "public");

            migrationBuilder.DropTable(
                name: "PermissionGrantChangeRequests");

            migrationBuilder.DropTable(
                name: "ResourcePermissionAssignmentOperations");

            migrationBuilder.DropTable(
                name: "PermissionDelegationDefinitionVersions");

            migrationBuilder.DropTable(
                name: "PermissionDelegationDefinitions");
        }
    }
}
