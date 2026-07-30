using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Tests.Data.StatsSchemaProof.Migrations
{
    /// <inheritdoc />
    public partial class StatsSchemaProofSecondStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "chars",
                schema: "gateway_stats",
                table: "proof_delta",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "proof_meta",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proof_meta", x => new { x.tenant, x.name });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proof_meta",
                schema: "gateway_stats");

            migrationBuilder.DropColumn(
                name: "chars",
                schema: "gateway_stats",
                table: "proof_delta");
        }
    }
}
