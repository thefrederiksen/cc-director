using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnLogSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "turn_log_switches",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    machine = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    recorded_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_log_switches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_turn_log_switches_account_machine",
                schema: "gateway",
                table: "turn_log_switches",
                columns: new[] { "account", "machine" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "turn_log_switches",
                schema: "gateway");
        }
    }
}
