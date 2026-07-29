using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SkillDirectoryFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedTools",
                schema: "gateway",
                table: "skill_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Compatibility",
                schema: "gateway",
                table: "skill_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "License",
                schema: "gateway",
                table: "skill_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                schema: "gateway",
                table: "skill_versions",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Encoding",
                schema: "gateway",
                table: "skill_files",
                type: "text",
                nullable: false,
                defaultValue: "utf8");

            migrationBuilder.AddColumn<bool>(
                name: "Executable",
                schema: "gateway",
                table: "skill_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedTools",
                schema: "gateway",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "Compatibility",
                schema: "gateway",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "License",
                schema: "gateway",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "Metadata",
                schema: "gateway",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "Encoding",
                schema: "gateway",
                table: "skill_files");

            migrationBuilder.DropColumn(
                name: "Executable",
                schema: "gateway",
                table: "skill_files");
        }
    }
}
