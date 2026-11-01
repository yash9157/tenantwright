using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tenant.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class TenantConnectionStringTargetFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetFingerprint",
                table: "TenantConnectionStrings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantConnectionStrings_TargetFingerprint",
                table: "TenantConnectionStrings",
                column: "TargetFingerprint",
                unique: true,
                filter: "\"TargetFingerprint\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantConnectionStrings_TargetFingerprint",
                table: "TenantConnectionStrings");

            migrationBuilder.DropColumn(
                name: "TargetFingerprint",
                table: "TenantConnectionStrings");
        }
    }
}
