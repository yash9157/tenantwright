using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDispatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationRecipients_NotificationId_RecipientType_Recipie~",
                table: "NotificationRecipients");

            migrationBuilder.DropIndex(
                name: "IX_NotificationDigestBuckets_Scope_Channel_Recipient_Period",
                table: "NotificationDigestBuckets");

            migrationBuilder.AddColumn<Guid>(
                name: "DispatchScopeId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DispatchStepId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RecipientValue",
                table: "NotificationRecipients",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<string>(
                name: "RecipientValueHash",
                table: "NotificationRecipients",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "RecipientValue",
                table: "NotificationDigestBuckets",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<string>(
                name: "RecipientValueHash",
                table: "NotificationDigestBuckets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Existing destinations must be backfilled before replacing their unique indexes.
            migrationBuilder.Sql("""
                UPDATE "NotificationRecipients"
                SET "RecipientValueHash" = upper(encode(sha256(convert_to("RecipientValue", 'UTF8')), 'hex'));
                UPDATE "NotificationDigestBuckets"
                SET "RecipientValueHash" = upper(encode(sha256(convert_to("RecipientValue", 'UTF8')), 'hex'));
                """);

            migrationBuilder.CreateTable(
                name: "ArchivedNotificationDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDispatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedNotificationDispatches", x => x.Id);
                    table.CheckConstraint("CK_ArchivedNotificationDispatches_Scope", "\"ScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000') AND (\"TenantId\" IS NULL OR \"TenantId\" <> '00000000-0000-0000-0000-000000000000')");
                });

            migrationBuilder.CreateTable(
                name: "NotificationDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceService = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ActivationTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextEvaluationAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_NotificationDispatches", x => x.Id);
                    table.UniqueConstraint("AK_NotificationDispatches_ScopeId_Id", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_NotificationDispatches_Lifetime", "\"ExpiresAtUtc\" > \"ActivationTimeUtc\"");
                    table.CheckConstraint("CK_NotificationDispatches_Scope", "\"ScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000') AND (\"TenantId\" IS NULL OR \"TenantId\" <> '00000000-0000-0000-0000-000000000000')");
                });

            migrationBuilder.CreateTable(
                name: "NotificationDispatchEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DispatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatchEvents", x => x.Id);
                    table.CheckConstraint("CK_NotificationDispatchEvents_Scope", "\"ScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000') AND (\"TenantId\" IS NULL OR \"TenantId\" <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_NotificationDispatchEvents_NotificationDispatches_ScopeId_D~",
                        columns: x => new { x.ScopeId, x.DispatchId },
                        principalTable: "NotificationDispatches",
                        principalColumns: new[] { "ScopeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDispatchRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DispatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PreferenceUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatchRecipients", x => x.Id);
                    table.UniqueConstraint("AK_NotificationDispatchRecipients_ScopeId_DispatchId_Id", x => new { x.ScopeId, x.DispatchId, x.Id });
                    table.CheckConstraint("CK_NotificationDispatchRecipients_Scope", "\"ScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000') AND (\"TenantId\" IS NULL OR \"TenantId\" <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_NotificationDispatchRecipients_NotificationDispatches_Scope~",
                        columns: x => new { x.ScopeId, x.DispatchId },
                        principalTable: "NotificationDispatches",
                        principalColumns: new[] { "ScopeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDispatchSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DispatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ContentJson = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SchedulingClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextEvaluationAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutcomeReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsPreparationDeferred = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatchSteps", x => x.Id);
                    table.UniqueConstraint("AK_NotificationDispatchSteps_ScopeId_Id", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_NotificationDispatchSteps_Ordinal", "\"Ordinal\" >= 0 AND \"Ordinal\" < 5");
                    table.CheckConstraint("CK_NotificationDispatchSteps_Scope", "\"ScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000') AND (\"TenantId\" IS NULL OR \"TenantId\" <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_NotificationDispatchSteps_NotificationDispatchRecipients_Sc~",
                        columns: x => new { x.ScopeId, x.DispatchId, x.RecipientId },
                        principalTable: "NotificationDispatchRecipients",
                        principalColumns: new[] { "ScopeId", "DispatchId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DispatchScopeId_DispatchStepId",
                table: "Notifications",
                columns: new[] { "DispatchScopeId", "DispatchStepId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DispatchStepId",
                table: "Notifications",
                column: "DispatchStepId",
                unique: true,
                filter: "\"DispatchStepId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_DispatchScope",
                table: "Notifications",
                sql: "(\"DispatchStepId\" IS NULL AND \"DispatchScopeId\" IS NULL) OR (\"DispatchStepId\" IS NOT NULL AND \"DispatchScopeId\" IS NOT NULL AND \"DispatchScopeId\" = COALESCE(\"TenantId\", '00000000-0000-0000-0000-000000000000'))");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_NotificationId_RecipientType_Recipie~",
                table: "NotificationRecipients",
                columns: new[] { "NotificationId", "RecipientType", "RecipientValueHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDigestBuckets_Scope_Channel_Recipient_Period",
                table: "NotificationDigestBuckets",
                columns: new[] { "ScopeKey", "Channel", "RecipientType", "RecipientValueHash", "Frequency", "PeriodStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedNotificationDispatches_ScopeId_SourceDispatchId_Sou~",
                table: "ArchivedNotificationDispatches",
                columns: new[] { "ScopeId", "SourceDispatchId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatches_NextEvaluationAtUtc_Id",
                table: "NotificationDispatches",
                columns: new[] { "NextEvaluationAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatches_ScopeId_RequestId",
                table: "NotificationDispatches",
                columns: new[] { "ScopeId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatchEvents_ScopeId_DispatchId_Revision",
                table: "NotificationDispatchEvents",
                columns: new[] { "ScopeId", "DispatchId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatchRecipients_ScopeId_DispatchId_Recipient~",
                table: "NotificationDispatchRecipients",
                columns: new[] { "ScopeId", "DispatchId", "RecipientKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatchSteps_ScopeId_DispatchId_RecipientId",
                table: "NotificationDispatchSteps",
                columns: new[] { "ScopeId", "DispatchId", "RecipientId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatchSteps_ScopeId_RecipientId_Channel",
                table: "NotificationDispatchSteps",
                columns: new[] { "ScopeId", "RecipientId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatchSteps_ScopeId_RecipientId_Ordinal",
                table: "NotificationDispatchSteps",
                columns: new[] { "ScopeId", "RecipientId", "Ordinal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_NotificationDispatchSteps_DispatchScopeId_Dis~",
                table: "Notifications",
                columns: new[] { "DispatchScopeId", "DispatchStepId" },
                principalTable: "NotificationDispatchSteps",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Roll back only an unused schema. Populated dispatch history or large destinations require a forward fix.
            migrationBuilder.Sql("""
                DO $migrationguard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "NotificationDispatches") OR
                       EXISTS (SELECT 1 FROM "ArchivedNotificationDispatches") OR
                       EXISTS (SELECT 1 FROM "NotificationRecipients" WHERE length("RecipientValue") > 256) OR
                       EXISTS (SELECT 1 FROM "NotificationDigestBuckets" WHERE length("RecipientValue") > 256)
                    THEN
                        RAISE EXCEPTION 'Notification dispatch rollback would discard retained data; use a forward corrective migration.';
                    END IF;
                END $migrationguard$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_NotificationDispatchSteps_DispatchScopeId_Dis~",
                table: "Notifications");

            migrationBuilder.DropTable(
                name: "ArchivedNotificationDispatches");

            migrationBuilder.DropTable(
                name: "NotificationDispatchEvents");

            migrationBuilder.DropTable(
                name: "NotificationDispatchSteps");

            migrationBuilder.DropTable(
                name: "NotificationDispatchRecipients");

            migrationBuilder.DropTable(
                name: "NotificationDispatches");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_DispatchScopeId_DispatchStepId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_DispatchStepId",
                table: "Notifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_DispatchScope",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_NotificationRecipients_NotificationId_RecipientType_Recipie~",
                table: "NotificationRecipients");

            migrationBuilder.DropIndex(
                name: "IX_NotificationDigestBuckets_Scope_Channel_Recipient_Period",
                table: "NotificationDigestBuckets");

            migrationBuilder.DropColumn(
                name: "DispatchScopeId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DispatchStepId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "RecipientValueHash",
                table: "NotificationRecipients");

            migrationBuilder.DropColumn(
                name: "RecipientValueHash",
                table: "NotificationDigestBuckets");

            migrationBuilder.AlterColumn<string>(
                name: "RecipientValue",
                table: "NotificationRecipients",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096);

            migrationBuilder.AlterColumn<string>(
                name: "RecipientValue",
                table: "NotificationDigestBuckets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_NotificationId_RecipientType_Recipie~",
                table: "NotificationRecipients",
                columns: new[] { "NotificationId", "RecipientType", "RecipientValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDigestBuckets_Scope_Channel_Recipient_Period",
                table: "NotificationDigestBuckets",
                columns: new[] { "ScopeKey", "Channel", "RecipientType", "RecipientValue", "Frequency", "PeriodStartUtc" },
                unique: true);
        }
    }
}
