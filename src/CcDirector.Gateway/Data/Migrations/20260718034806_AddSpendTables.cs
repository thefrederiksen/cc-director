using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpendTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_hosted_ai_spend",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    TransactionCreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_hosted_ai_spend", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_spend",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentKind = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    RepoPath = table.Column<string>(type: "TEXT", nullable: true),
                    TokensCaptured = table.Column<bool>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    BillingMode = table.Column<string>(type: "TEXT", nullable: false),
                    MeteredCostMicros = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_spend", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_hosted_ai_spend_tenant_id",
                table: "account_hosted_ai_spend",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_account_hosted_ai_spend_tenant_id_Kind_AmountMicros_TransactionCreatedUtc",
                table: "account_hosted_ai_spend",
                columns: new[] { "tenant_id", "Kind", "AmountMicros", "TransactionCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_account_hosted_ai_spend_tenant_id_TransactionCreatedUtc",
                table: "account_hosted_ai_spend",
                columns: new[] { "tenant_id", "TransactionCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_spend_tenant_id",
                table: "session_spend",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_spend_tenant_id_LastObservedUtc",
                table: "session_spend",
                columns: new[] { "tenant_id", "LastObservedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_hosted_ai_spend");

            migrationBuilder.DropTable(
                name: "session_spend");
        }
    }
}
