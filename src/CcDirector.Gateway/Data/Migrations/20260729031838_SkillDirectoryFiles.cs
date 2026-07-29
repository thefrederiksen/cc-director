using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class SkillDirectoryFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedTools",
                table: "skill_versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Compatibility",
                table: "skill_versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "License",
                table: "skill_versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "skill_versions",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Encoding",
                table: "skill_files",
                type: "TEXT",
                nullable: false,
                defaultValue: "utf8");

            migrationBuilder.AddColumn<bool>(
                name: "Executable",
                table: "skill_files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedTools",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "Compatibility",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "License",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "skill_versions");

            migrationBuilder.DropColumn(
                name: "Encoding",
                table: "skill_files");

            migrationBuilder.DropColumn(
                name: "Executable",
                table: "skill_files");
        }
    }
}
