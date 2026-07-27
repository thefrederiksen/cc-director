using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCostAndInterruptionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CacheCreationTokens",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CacheReadTokens",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputCharacterCount",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MissionId",
                schema: "gateway",
                table: "session_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PeakContextTokens",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WaitingStretchCount",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheCreationTokens",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "InputCharacterCount",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "MissionId",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "PeakContextTokens",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "WaitingStretchCount",
                schema: "gateway",
                table: "session_history");
        }
    }
}
