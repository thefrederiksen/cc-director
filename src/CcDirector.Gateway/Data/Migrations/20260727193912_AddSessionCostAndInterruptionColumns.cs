using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCostAndInterruptionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CacheCreationTokens",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CacheReadTokens",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputCharacterCount",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MissionId",
                table: "session_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PeakContextTokens",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WaitingStretchCount",
                table: "session_history",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheCreationTokens",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "InputCharacterCount",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "MissionId",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "PeakContextTokens",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "WaitingStretchCount",
                table: "session_history");
        }
    }
}
