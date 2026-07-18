using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPushSubscriptionsAndWingmanInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", nullable: false),
                    Auth = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_subscriptions", x => x.Endpoint);
                });

            migrationBuilder.CreateTable(
                name: "wingman_instructions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActiveVersionId = table.Column<string>(type: "TEXT", nullable: true),
                    AckDefaultVersion = table.Column<string>(type: "TEXT", nullable: false),
                    AckDefaultContent = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Versions = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wingman_instructions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_tenant_id",
                table: "push_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_wingman_instructions_tenant_id",
                table: "wingman_instructions",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_subscriptions");

            migrationBuilder.DropTable(
                name: "wingman_instructions");
        }
    }
}
