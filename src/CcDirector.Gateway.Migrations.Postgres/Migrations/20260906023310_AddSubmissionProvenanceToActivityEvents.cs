using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionProvenanceToActivityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContentLength",
                schema: "gateway",
                table: "activity_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSha256",
                schema: "gateway",
                table: "activity_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityKind",
                schema: "gateway",
                table: "activity_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Route",
                schema: "gateway",
                table: "activity_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpokenSpans",
                schema: "gateway",
                table: "activity_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscriptId",
                schema: "gateway",
                table: "activity_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentLength",
                schema: "gateway",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "ContentSha256",
                schema: "gateway",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "IdentityKind",
                schema: "gateway",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "Route",
                schema: "gateway",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "SpokenSpans",
                schema: "gateway",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "TranscriptId",
                schema: "gateway",
                table: "activity_events");
        }
    }
}
