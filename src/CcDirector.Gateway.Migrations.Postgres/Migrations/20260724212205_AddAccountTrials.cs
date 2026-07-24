using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountTrials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_trials",
                schema: "gateway",
                columns: table => new
                {
                    subject = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_trials", x => x.subject);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_trials_expires_at_utc",
                schema: "gateway",
                table: "account_trials",
                column: "expires_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_trials",
                schema: "gateway");
        }
    }
}
