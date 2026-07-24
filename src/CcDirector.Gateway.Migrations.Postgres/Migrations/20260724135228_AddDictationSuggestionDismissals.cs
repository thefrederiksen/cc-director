using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDictationSuggestionDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictation_suggestion_dismissals",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Term = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    DisplayTerm = table.Column<string>(type: "text", nullable: false),
                    WrongCount = table.Column<int>(type: "integer", nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false),
                    VariantsJson = table.Column<string>(type: "text", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictation_suggestion_dismissals", x => new { x.tenant_id, x.Term });
                });

            migrationBuilder.CreateIndex(
                name: "IX_dictation_suggestion_dismissals_tenant_id",
                schema: "gateway",
                table: "dictation_suggestion_dismissals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dictation_suggestion_dismissals_tenant_id_DismissedAtUtc",
                schema: "gateway",
                table: "dictation_suggestion_dismissals",
                columns: new[] { "tenant_id", "DismissedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dictation_suggestion_dismissals",
                schema: "gateway");
        }
    }
}
