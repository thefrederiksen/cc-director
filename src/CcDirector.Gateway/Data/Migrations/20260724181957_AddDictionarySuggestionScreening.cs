using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDictionarySuggestionScreening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictation_suggestion_scans",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    ScannedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScreeningOk = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScreeningError = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestionsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictation_suggestion_scans", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "dictation_suggestion_verdicts",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Term = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayTerm = table.Column<string>(type: "TEXT", nullable: false),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    JudgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictation_suggestion_verdicts", x => new { x.tenant_id, x.Term });
                });

            migrationBuilder.CreateIndex(
                name: "IX_dictation_suggestion_scans_tenant_id",
                table: "dictation_suggestion_scans",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dictation_suggestion_verdicts_tenant_id",
                table: "dictation_suggestion_verdicts",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dictation_suggestion_scans");

            migrationBuilder.DropTable(
                name: "dictation_suggestion_verdicts");
        }
    }
}
