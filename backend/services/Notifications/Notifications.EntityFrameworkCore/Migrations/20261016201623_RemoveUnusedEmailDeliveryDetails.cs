using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedEmailDeliveryDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailDeliveryDetails");

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "Notifications",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 2);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 2,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateTable(
                name: "EmailDeliveryDetails",
                columns: table => new
                {
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    Bcc = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Cc = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FromName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDeliveryDetails", x => x.DeliveryId);
                    table.ForeignKey(
                        name: "FK_EmailDeliveryDetails_NotificationDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "NotificationDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
