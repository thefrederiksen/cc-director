using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_turn_heads",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Generation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GenerationSource = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    GenerationStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Agent = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsSupported = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRawText = table.Column<bool>(type: "INTEGER", nullable: false),
                    HistoryState = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_turn_heads", x => new { x.tenant_id, x.SessionId });
                });

            migrationBuilder.CreateTable(
                name: "session_turns",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Generation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PartsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ContextId = table.Column<string>(type: "TEXT", nullable: true),
                    IsMeta = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSidechain = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_turns", x => new { x.tenant_id, x.SessionId, x.Generation, x.Ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_turn_heads_tenant_id",
                table: "session_turn_heads",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_turn_heads_tenant_id_DirectorId",
                table: "session_turn_heads",
                columns: new[] { "tenant_id", "DirectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_session_turn_heads_tenant_id_UpdatedAtUtc",
                table: "session_turn_heads",
                columns: new[] { "tenant_id", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_turns_tenant_id",
                table: "session_turns",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_turns_tenant_id_ReceivedAtUtc",
                table: "session_turns",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_turn_heads");

            migrationBuilder.DropTable(
                name: "session_turns");
        }
    }
}
