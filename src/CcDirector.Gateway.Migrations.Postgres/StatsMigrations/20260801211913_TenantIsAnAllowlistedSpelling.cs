using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.StatsMigrations
{
    /// <summary>
    /// Replaces the tenant guard on every statistics table with an ALLOWLIST of the characters a tenant
    /// may contain: <c>^[a-z0-9-]+$</c>, anchored at both ends.
    ///
    /// WHY AN ALLOWLIST. Three previous predicates each tried to exclude whitespace and each left a gap -
    /// <c>tenant &lt;&gt; ''</c> passed a space, <c>btrim(tenant) &lt;&gt; ''</c> passed a tab (PostgreSQL's
    /// one-argument btrim strips only the space character), and <c>tenant ~ '[^[:space:]]'</c> passed the
    /// four characters .NET calls whitespace and POSIX does not: U+0085, U+00A0, U+2007, U+202F. Mirroring
    /// one runtime's idea of whitespace with another's does not converge. An allowlist refuses every
    /// spelling that names nobody, including ones nobody has thought of, as a side effect.
    ///
    /// The set is DERIVED from the four production construction sites: <c>TenantId.Local</c> ("local"),
    /// <c>TenantId.System</c> ("system"), a real account (<c>Guid.NewGuid().ToString()</c>, lower-case hex
    /// and hyphens), and SkillStore's library partition, which is whichever of Local or System was ambient.
    ///
    /// IT IS DELIBERATELY NARROWER THAN <c>TenantId</c>. TenantId only TRIMS - it does not lower-case and
    /// does not restrict characters - so <c>new TenantId("Alice")</c> is legal in the product and refused
    /// here. Accepted knowingly: no production path yields such a value, and the failure mode is a named
    /// constraint violation at insert, loud and at development time, rather than silent mispartitioning or
    /// a wrong-tenant read.
    ///
    /// SO IF YOU ARE HERE BECAUSE OF A VIOLATION: the schema is probably not broken. A writer used a
    /// spelling production has never produced. Fix the caller, unless a real production mint has genuinely
    /// started producing something this excludes - in which case widen the allowlist, and never soften it
    /// back into "not whitespace".
    /// </summary>
    public partial class TenantIsAnAllowlistedSpelling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddCheckConstraint(
                name: "ck_wingman_session_tenant_not_empty",
                schema: "gateway_stats",
                table: "wingman_session",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_token_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "token_highwater",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_token_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "token_delta",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stat_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "stat_delta",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "session_highwater",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_repo_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "repo_identity",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "model_identity",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_meta_tenant_not_empty",
                schema: "gateway_stats",
                table: "meta",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ConcurrencyPeaks_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_peak",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ConcurrencyHourMembers_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_hour_member",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ConcurrencyHours_tenant_not_empty",
                schema: "gateway_stats",
                table: "concurrency_hour",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_checkout_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "checkout_identity",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_seeded_tenant_not_empty",
                schema: "gateway_stats",
                table: "agents_seeded",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_identity_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_identity",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_driven_highwater_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_driven_highwater",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_driven_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_driven_delta",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_delta_tenant_not_empty",
                schema: "gateway_stats",
                table: "agent_delta",
                sql: "\"tenant\" ~ '^[a-z0-9-]+$'");
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
    }
}
