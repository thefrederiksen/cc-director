using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSupervisionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AgentTurnCount",
                schema: "gateway",
                table: "session_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CumulativeIdleSeconds",
                schema: "gateway",
                table: "session_history",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentTurnCount",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "CumulativeIdleSeconds",
                schema: "gateway",
                table: "session_history");
        }
    }
}
