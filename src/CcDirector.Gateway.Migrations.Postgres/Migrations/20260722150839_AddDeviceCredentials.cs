using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_credentials",
                schema: "gateway",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    DeviceKeyHash = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    KeyLast4 = table.Column<string>(type: "text", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    DeviceType = table.Column<string>(type: "text", nullable: false),
                    CloudDeviceId = table.Column<string>(type: "text", nullable: true),
                    AccountSubject = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_credentials", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "device_import_markers",
                schema: "gateway",
                columns: table => new
                {
                    SourcePath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ImportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_import_markers", x => x.SourcePath);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_credentials_DeviceKeyHash",
                schema: "gateway",
                table: "device_credentials",
                column: "DeviceKeyHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_credentials",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "device_import_markers",
                schema: "gateway");
        }
    }
}
