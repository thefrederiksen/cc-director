using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSnoozes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "snoozes",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    SnoozeUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: true),
                    PendingMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    OwnerTurnBaselineUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snoozes", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_snoozes_tenant_id",
                table: "snoozes",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "snoozes");
        }
    }
}
