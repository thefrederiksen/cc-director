using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrialExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trial_extensions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    subject = table.Column<string>(type: "TEXT", nullable: false),
                    member_email = table.Column<string>(type: "TEXT", nullable: true),
                    started_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    previous_expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    new_expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    actor = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    recorded_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trial_extensions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trial_extensions_subject",
                table: "trial_extensions",
                column: "subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trial_extensions");
        }
    }
}
