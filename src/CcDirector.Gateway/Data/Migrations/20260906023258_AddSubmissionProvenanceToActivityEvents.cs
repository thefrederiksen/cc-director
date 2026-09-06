using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionProvenanceToActivityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContentLength",
                table: "activity_events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSha256",
                table: "activity_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityKind",
                table: "activity_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Route",
                table: "activity_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpokenSpans",
                table: "activity_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscriptId",
                table: "activity_events",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentLength",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "ContentSha256",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "IdentityKind",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "Route",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "SpokenSpans",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "TranscriptId",
                table: "activity_events");
        }
    }
}
