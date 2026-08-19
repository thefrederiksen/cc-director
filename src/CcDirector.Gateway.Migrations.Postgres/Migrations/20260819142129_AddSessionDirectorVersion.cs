using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionDirectorVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectorVersion",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectorVersion",
                schema: "gateway",
                table: "session_history");
        }
    }
}
