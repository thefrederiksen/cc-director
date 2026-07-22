using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CallerSuppliedKeysScopedByTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_snoozes",
                schema: "gateway",
                table: "snoozes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_session_spend",
                schema: "gateway",
                table: "session_spend");

            migrationBuilder.DropPrimaryKey(
                name: "PK_push_subscriptions",
                schema: "gateway",
                table: "push_subscriptions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_snoozes",
                schema: "gateway",
                table: "snoozes",
                columns: new[] { "tenant_id", "SessionId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_session_spend",
                schema: "gateway",
                table: "session_spend",
                columns: new[] { "tenant_id", "SessionId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_push_subscriptions",
                schema: "gateway",
                table: "push_subscriptions",
                columns: new[] { "tenant_id", "Endpoint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_snoozes",
                schema: "gateway",
                table: "snoozes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_session_spend",
                schema: "gateway",
                table: "session_spend");

            migrationBuilder.DropPrimaryKey(
                name: "PK_push_subscriptions",
                schema: "gateway",
                table: "push_subscriptions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_snoozes",
                schema: "gateway",
                table: "snoozes",
                column: "SessionId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_session_spend",
                schema: "gateway",
                table: "session_spend",
                column: "SessionId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_push_subscriptions",
                schema: "gateway",
                table: "push_subscriptions",
                column: "Endpoint");
        }
    }
}
