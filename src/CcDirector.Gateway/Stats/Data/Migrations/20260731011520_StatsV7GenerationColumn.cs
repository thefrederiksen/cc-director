using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <inheritdoc />
    public partial class StatsV7GenerationColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "session_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "agent_driven_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        
            // THE VERSION STAMP IS PART OF THE SCHEMA. This migration is schema version 7, so it moves the stamp the hand-rolled path moves at MigrateToVersion7. An older build reads this to decide whether it understands the file at all; leaving it behind lets that build run its own steps against tables that already exist.
            migrationBuilder.Sql("PRAGMA user_version = 7");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "generation",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "agent_driven_highwater");
        
            // Down puts the stamp back to 6, so reverting this migration leaves a file an older build still recognises.
            migrationBuilder.Sql("PRAGMA user_version = 6");
        }
    }
}
