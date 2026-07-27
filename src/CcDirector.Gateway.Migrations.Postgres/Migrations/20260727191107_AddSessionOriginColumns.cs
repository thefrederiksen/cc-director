using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionOriginColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginSurface",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentSessionId",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginKind",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "OriginSurface",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "ParentSessionId",
                schema: "gateway",
                table: "session_history");
        }
    }
}
