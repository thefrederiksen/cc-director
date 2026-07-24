using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDictationSuggestionDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictation_suggestion_dismissals",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Term = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayTerm = table.Column<string>(type: "TEXT", nullable: false),
                    WrongCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    VariantsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictation_suggestion_dismissals", x => new { x.tenant_id, x.Term });
                });

            migrationBuilder.CreateIndex(
                name: "IX_dictation_suggestion_dismissals_tenant_id",
                table: "dictation_suggestion_dismissals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dictation_suggestion_dismissals_tenant_id_DismissedAtUtc",
                table: "dictation_suggestion_dismissals",
                columns: new[] { "tenant_id", "DismissedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dictation_suggestion_dismissals");
        }
    }
}
