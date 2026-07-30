using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CcDirector.Gateway.Tests.Data.StatsSchemaProof.Migrations
{
    /// <inheritdoc />
    public partial class StatsSchemaProofBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gateway_stats");

            migrationBuilder.CreateTable(
                name: "proof_delta",
                schema: "gateway_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hour_utc = table.Column<string>(type: "text", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false),
                    turns = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proof_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "proof_highwater",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    turns = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proof_highwater", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_proof_delta_tenant_hour",
                schema: "gateway_stats",
                table: "proof_delta",
                columns: new[] { "tenant", "hour_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proof_delta",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "proof_highwater",
                schema: "gateway_stats");
        }
    }
}
