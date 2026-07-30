using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.StatsMigrations
{
    /// <inheritdoc />
    public partial class InitialGatewayStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gateway_stats");

            migrationBuilder.CreateTable(
                name: "agent_delta",
                schema: "gateway_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agent_id = table.Column<long>(type: "bigint", nullable: false),
                    is_voice = table.Column<bool>(type: "boolean", nullable: false),
                    turns = table.Column<long>(type: "bigint", nullable: false),
                    chars = table.Column<long>(type: "bigint", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_driven_delta",
                schema: "gateway_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agent_id = table.Column<long>(type: "bigint", nullable: false),
                    turns = table.Column<long>(type: "bigint", nullable: false),
                    chars = table.Column<long>(type: "bigint", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_driven_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_driven_highwater",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    turns = table.Column<long>(type: "bigint", nullable: false),
                    chars = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_driven_highwater", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "agent_identity",
                schema: "gateway_stats",
                columns: table => new
                {
                    agent_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agent_display = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_identity", x => x.agent_id);
                });

            migrationBuilder.CreateTable(
                name: "agent_session",
                schema: "gateway_stats",
                columns: table => new
                {
                    agent_id = table.Column<long>(type: "bigint", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_session", x => new { x.agent_id, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "agents_seeded",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents_seeded", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "checkout_identity",
                schema: "gateway_stats",
                columns: table => new
                {
                    checkout_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    checkout_display = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_identity", x => x.checkout_id);
                });

            migrationBuilder.CreateTable(
                name: "meta",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    value = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta", x => new { x.tenant, x.name });
                });

            migrationBuilder.CreateTable(
                name: "model_identity",
                schema: "gateway_stats",
                columns: table => new
                {
                    model_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    model_display = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_identity", x => x.model_id);
                });

            migrationBuilder.CreateTable(
                name: "repo_identity",
                schema: "gateway_stats",
                columns: table => new
                {
                    repo_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    repo_display = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_identity", x => x.repo_id);
                });

            migrationBuilder.CreateTable(
                name: "repo_session",
                schema: "gateway_stats",
                columns: table => new
                {
                    repo_id = table.Column<long>(type: "bigint", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_session", x => new { x.repo_id, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "session_highwater",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    modality = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    surface = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    turns = table.Column<long>(type: "bigint", nullable: false),
                    chars = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_highwater", x => new { x.tenant, x.session_id, x.modality, x.surface });
                });

            migrationBuilder.CreateTable(
                name: "stat_delta",
                schema: "gateway_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hour_utc = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    modality = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    surface = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    is_voice = table.Column<bool>(type: "boolean", nullable: false),
                    repo_id = table.Column<long>(type: "bigint", nullable: false),
                    wingman = table.Column<bool>(type: "boolean", nullable: false),
                    turns = table.Column<long>(type: "bigint", nullable: false),
                    chars = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<long>(type: "bigint", nullable: true),
                    checkout_id = table.Column<long>(type: "bigint", nullable: true),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stat_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "token_delta",
                schema: "gateway_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hour_utc = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    model_id = table.Column<long>(type: "bigint", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_creation_tokens = table.Column<long>(type: "bigint", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "token_highwater",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_creation_tokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_highwater", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "wingman_session",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wingman_session", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_stat_delta_hour",
                schema: "gateway_stats",
                table: "stat_delta",
                column: "hour_utc");

            migrationBuilder.CreateIndex(
                name: "ix_stat_delta_tenant_hour",
                schema: "gateway_stats",
                table: "stat_delta",
                columns: new[] { "tenant", "hour_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_token_delta_hour",
                schema: "gateway_stats",
                table: "token_delta",
                column: "hour_utc");

            migrationBuilder.CreateIndex(
                name: "ix_token_delta_tenant_hour",
                schema: "gateway_stats",
                table: "token_delta",
                columns: new[] { "tenant", "hour_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_delta",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "agent_driven_delta",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "agent_driven_highwater",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "agent_identity",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "agent_session",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "agents_seeded",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "checkout_identity",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "meta",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "model_identity",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "repo_identity",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "repo_session",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "session_highwater",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "stat_delta",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "token_delta",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "token_highwater",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "wingman_session",
                schema: "gateway_stats");
        }
    }
}
