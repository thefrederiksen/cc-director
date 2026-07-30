using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialGatewayStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_delta",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    agent_id = table.Column<long>(type: "INTEGER", nullable: false),
                    is_voice = table.Column<bool>(type: "INTEGER", nullable: false),
                    turns = table.Column<long>(type: "INTEGER", nullable: false),
                    chars = table.Column<long>(type: "INTEGER", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_driven_delta",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    agent_id = table.Column<long>(type: "INTEGER", nullable: false),
                    turns = table.Column<long>(type: "INTEGER", nullable: false),
                    chars = table.Column<long>(type: "INTEGER", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_driven_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_driven_highwater",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    turns = table.Column<long>(type: "INTEGER", nullable: false),
                    chars = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_driven_highwater", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "agent_identity",
                columns: table => new
                {
                    agent_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    agent_display = table.Column<string>(type: "TEXT", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_identity", x => x.agent_id);
                });

            migrationBuilder.CreateTable(
                name: "agent_session",
                columns: table => new
                {
                    agent_id = table.Column<long>(type: "INTEGER", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_session", x => new { x.agent_id, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "agents_seeded",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents_seeded", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "checkout_identity",
                columns: table => new
                {
                    checkout_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    checkout_display = table.Column<string>(type: "TEXT", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_identity", x => x.checkout_id);
                });

            migrationBuilder.CreateTable(
                name: "meta",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta", x => new { x.tenant, x.name });
                });

            migrationBuilder.CreateTable(
                name: "model_identity",
                columns: table => new
                {
                    model_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    model_display = table.Column<string>(type: "TEXT", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_identity", x => x.model_id);
                });

            migrationBuilder.CreateTable(
                name: "repo_identity",
                columns: table => new
                {
                    repo_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    repo_display = table.Column<string>(type: "TEXT", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_identity", x => x.repo_id);
                });

            migrationBuilder.CreateTable(
                name: "repo_session",
                columns: table => new
                {
                    repo_id = table.Column<long>(type: "INTEGER", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_session", x => new { x.repo_id, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "session_highwater",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    modality = table.Column<string>(type: "TEXT", nullable: false),
                    surface = table.Column<string>(type: "TEXT", nullable: false),
                    turns = table.Column<long>(type: "INTEGER", nullable: false),
                    chars = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_highwater", x => new { x.tenant, x.session_id, x.modality, x.surface });
                });

            migrationBuilder.CreateTable(
                name: "stat_delta",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    hour_utc = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    modality = table.Column<string>(type: "TEXT", nullable: false),
                    surface = table.Column<string>(type: "TEXT", nullable: false),
                    is_voice = table.Column<bool>(type: "INTEGER", nullable: false),
                    repo_id = table.Column<long>(type: "INTEGER", nullable: false),
                    wingman = table.Column<bool>(type: "INTEGER", nullable: false),
                    turns = table.Column<long>(type: "INTEGER", nullable: false),
                    chars = table.Column<long>(type: "INTEGER", nullable: false),
                    model_id = table.Column<long>(type: "INTEGER", nullable: true),
                    checkout_id = table.Column<long>(type: "INTEGER", nullable: true),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stat_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "token_delta",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    hour_utc = table.Column<string>(type: "TEXT", nullable: false),
                    model_id = table.Column<long>(type: "INTEGER", nullable: true),
                    input_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    output_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    cache_creation_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_delta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "token_highwater",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    input_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    output_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    cache_creation_tokens = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_highwater", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "wingman_session",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wingman_session", x => new { x.tenant, x.session_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_stat_delta_hour",
                table: "stat_delta",
                column: "hour_utc");

            migrationBuilder.CreateIndex(
                name: "ix_stat_delta_tenant_hour",
                table: "stat_delta",
                columns: new[] { "tenant", "hour_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_token_delta_hour",
                table: "token_delta",
                column: "hour_utc");

            migrationBuilder.CreateIndex(
                name: "ix_token_delta_tenant_hour",
                table: "token_delta",
                columns: new[] { "tenant", "hour_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_delta");

            migrationBuilder.DropTable(
                name: "agent_driven_delta");

            migrationBuilder.DropTable(
                name: "agent_driven_highwater");

            migrationBuilder.DropTable(
                name: "agent_identity");

            migrationBuilder.DropTable(
                name: "agent_session");

            migrationBuilder.DropTable(
                name: "agents_seeded");

            migrationBuilder.DropTable(
                name: "checkout_identity");

            migrationBuilder.DropTable(
                name: "meta");

            migrationBuilder.DropTable(
                name: "model_identity");

            migrationBuilder.DropTable(
                name: "repo_identity");

            migrationBuilder.DropTable(
                name: "repo_session");

            migrationBuilder.DropTable(
                name: "session_highwater");

            migrationBuilder.DropTable(
                name: "stat_delta");

            migrationBuilder.DropTable(
                name: "token_delta");

            migrationBuilder.DropTable(
                name: "token_highwater");

            migrationBuilder.DropTable(
                name: "wingman_session");
        }
    }
}
