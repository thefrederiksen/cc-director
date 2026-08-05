using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_keys",
                schema: "gateway",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    DirectorId = table.Column<string>(type: "text", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_keys", x => new { x.TenantId, x.SessionId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_keys_ExpiresAtUtc",
                schema: "gateway",
                table: "session_keys",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_session_keys_KeyHash",
                schema: "gateway",
                table: "session_keys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_keys",
                schema: "gateway");
        }
    }
}
