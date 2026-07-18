using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "workflows");
        }
    }
}
