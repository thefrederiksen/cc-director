using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.StatsMigrations
{
    /// <inheritdoc />
    public partial class TenantRequiredNoDefaultAndNonEmpty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "token_delta",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "stat_delta",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "repo_identity",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "model_identity",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "checkout_identity",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "agent_identity",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "agent_driven_delta",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "agent_delta",
                type: "text",
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "local",
                oldCollation: "C");

            migrationBuilder.AddCheckConstraint(
                name: "ck_wingman_session_tenant_not_empty",
                schema: "gateway_stats",
                table: "wingman_session",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_token_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "token_highwater",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_token_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "token_delta",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stat_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "stat_delta",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "session_highwater",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_repo_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "repo_identity",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "model_identity",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_meta_tenant_not_empty",
                schema: "gateway_stats",
                table: "meta",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ConcurrencyPeaks_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_peak",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ConcurrencyHourMembers_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_hour_member",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ConcurrencyHours_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_hour",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_checkout_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "checkout_identity",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_seeded_tenant_not_empty",
                schema: "gateway_stats",
                table: "agents_seeded",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_identity",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_driven_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_driven_highwater",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_driven_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_driven_delta",
                sql: "\"tenant\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_delta",
                sql: "\"tenant\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_wingman_session_tenant_not_empty",
                schema: "gateway_stats",
                table: "wingman_session");

            migrationBuilder.DropCheckConstraint(
                name: "ck_token_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "token_highwater");

            migrationBuilder.DropCheckConstraint(
                name: "ck_token_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "token_delta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stat_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "stat_delta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_session_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "session_highwater");

            migrationBuilder.DropCheckConstraint(
                name: "ck_repo_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "repo_identity");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "model_identity");

            migrationBuilder.DropCheckConstraint(
                name: "ck_meta_tenant_not_empty",
                schema: "gateway_stats",
                table: "meta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ConcurrencyPeaks_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_peak");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ConcurrencyHourMembers_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_hour_member");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ConcurrencyHours_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_hour");

            migrationBuilder.DropCheckConstraint(
                name: "ck_checkout_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "checkout_identity");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_seeded_tenant_not_empty",
                schema: "gateway_stats",
                table: "agents_seeded");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_identity");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_driven_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_driven_highwater");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_driven_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_driven_delta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_delta");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "token_delta",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "stat_delta",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "repo_identity",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "model_identity",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "checkout_identity",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "agent_identity",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "agent_driven_delta",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "tenant",
                schema: "gateway_stats",
                table: "agent_delta",
                type: "text",
                nullable: false,
                defaultValue: "local",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldCollation: "C");
        }
    }
}
