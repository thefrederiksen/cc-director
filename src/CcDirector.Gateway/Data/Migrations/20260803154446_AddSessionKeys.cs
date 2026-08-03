using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_keys",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_keys", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_keys_ExpiresAtUtc",
                table: "session_keys",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_session_keys_KeyHash",
                table: "session_keys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_keys");
        }
    }
}
