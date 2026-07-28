using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "skill_tenant_overrides",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_tenant_overrides", x => new { x.tenant_id, x.SkillId });
                });

            migrationBuilder.CreateTable(
                name: "skill_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Triggers = table.Column<string>(type: "TEXT", nullable: false),
                    BodyMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    AuthoredBy = table.Column<string>(type: "TEXT", nullable: false),
                    ChangeNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    LatestVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    ShippedContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => new { x.tenant_id, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_files_tenant_id",
                table: "skill_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_files_VersionId",
                table: "skill_files",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_skill_tenant_overrides_tenant_id",
                table: "skill_tenant_overrides",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_tenant_id",
                table: "skill_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_tenant_id_SkillId_Version",
                table: "skill_versions",
                columns: new[] { "tenant_id", "SkillId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_tenant_id",
                table: "skills",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_files");

            migrationBuilder.DropTable(
                name: "skill_tenant_overrides");

            migrationBuilder.DropTable(
                name: "skill_versions");

            migrationBuilder.DropTable(
                name: "skills");
        }
    }
}
