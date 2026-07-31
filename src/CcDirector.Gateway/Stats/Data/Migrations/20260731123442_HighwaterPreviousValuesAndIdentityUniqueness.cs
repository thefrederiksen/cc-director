using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <summary>
    /// Schema version 6 of the SQLite statistics store, as an Entity Framework migration: the previous-value
    /// columns on the three high-water tables and the four unique identity indexes - the same changes the
    /// shipped hand-rolled <c>GatewayStatsDatabase.MigrateToVersion6</c> applies, so a file upgraded by either
    /// path carries the same shape. Stamps <c>PRAGMA user_version = 6</c> in its Up() and resets it in its
    /// Down(), per the rule <c>GatewayStatsSqliteVersionStampTests</c> enforces: the stamp is what an OLDER
    /// build reads to refuse a newer file cleanly on a desktop rollback.
    /// </summary>
    public partial class HighwaterPreviousValuesAndIdentityUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "previous_cache_creation_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_cache_read_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_input_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_output_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_chars",
                table: "session_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_turns",
                table: "session_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_chars",
                table: "agent_driven_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_turns",
                table: "agent_driven_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ux_repo_identity_tenant_display",
                table: "repo_identity",
                columns: new[] { "tenant", "repo_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_model_identity_tenant_display",
                table: "model_identity",
                columns: new[] { "tenant", "model_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_checkout_identity_tenant_display",
                table: "checkout_identity",
                columns: new[] { "tenant", "checkout_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_agent_identity_tenant_display",
                table: "agent_identity",
                columns: new[] { "tenant", "agent_display" },
                unique: true);

            migrationBuilder.Sql("PRAGMA user_version = 6");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_repo_identity_tenant_display",
                table: "repo_identity");

            migrationBuilder.DropIndex(
                name: "ux_model_identity_tenant_display",
                table: "model_identity");

            migrationBuilder.DropIndex(
                name: "ux_checkout_identity_tenant_display",
                table: "checkout_identity");

            migrationBuilder.DropIndex(
                name: "ux_agent_identity_tenant_display",
                table: "agent_identity");

            migrationBuilder.DropColumn(
                name: "previous_cache_creation_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_cache_read_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_input_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_output_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_chars",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "previous_turns",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "previous_chars",
                table: "agent_driven_highwater");

            migrationBuilder.DropColumn(
                name: "previous_turns",
                table: "agent_driven_highwater");

            migrationBuilder.Sql("PRAGMA user_version = 5");
        }
    }
}
