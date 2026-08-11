using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTrialExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trial_extensions",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    member_email = table.Column<string>(type: "text", nullable: true),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    previous_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    new_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    recorded_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trial_extensions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trial_extensions_subject",
                schema: "gateway",
                table: "trial_extensions",
                column: "subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trial_extensions",
                schema: "gateway");
        }
    }
}
