using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_files",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "skill_tenant_overrides",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SkillId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_tenant_overrides", x => new { x.tenant_id, x.SkillId });
                });

            migrationBuilder.CreateTable(
                name: "skill_versions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Triggers = table.Column<List<string>>(type: "text[]", nullable: false),
                    BodyMarkdown = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    AuthoredBy = table.Column<string>(type: "text", nullable: false),
                    ChangeNote = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LatestVersion = table.Column<int>(type: "integer", nullable: false),
                    PublishedVersion = table.Column<int>(type: "integer", nullable: true),
                    ShippedContentHash = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => new { x.tenant_id, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_files_tenant_id",
                schema: "gateway",
                table: "skill_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_files_VersionId",
                schema: "gateway",
                table: "skill_files",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_skill_tenant_overrides_tenant_id",
                schema: "gateway",
                table: "skill_tenant_overrides",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_tenant_id",
                schema: "gateway",
                table: "skill_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_tenant_id_SkillId_Version",
                schema: "gateway",
                table: "skill_versions",
                columns: new[] { "tenant_id", "SkillId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_tenant_id",
                schema: "gateway",
                table: "skills",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_files",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "skill_tenant_overrides",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "skill_versions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "skills",
                schema: "gateway");
        }
    }
}
