using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_turn_heads",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DirectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Generation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    GenerationSource = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    GenerationStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Agent = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsSupported = table.Column<bool>(type: "boolean", nullable: false),
                    IsRawText = table.Column<bool>(type: "boolean", nullable: false),
                    HistoryState = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_turn_heads", x => new { x.tenant_id, x.SessionId });
                });

            migrationBuilder.CreateTable(
                name: "session_turns",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Generation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    DirectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PartsJson = table.Column<string>(type: "text", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContextId = table.Column<string>(type: "text", nullable: true),
                    IsMeta = table.Column<bool>(type: "boolean", nullable: false),
                    IsSidechain = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_turns", x => new { x.tenant_id, x.SessionId, x.Generation, x.Ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_turn_heads_tenant_id",
                schema: "gateway",
                table: "session_turn_heads",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_turn_heads_tenant_id_DirectorId",
                schema: "gateway",
                table: "session_turn_heads",
                columns: new[] { "tenant_id", "DirectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_session_turn_heads_tenant_id_UpdatedAtUtc",
                schema: "gateway",
                table: "session_turn_heads",
                columns: new[] { "tenant_id", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_turns_tenant_id",
                schema: "gateway",
                table: "session_turns",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_turns_tenant_id_ReceivedAtUtc",
                schema: "gateway",
                table: "session_turns",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_turn_heads",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "session_turns",
                schema: "gateway");
        }
    }
}
