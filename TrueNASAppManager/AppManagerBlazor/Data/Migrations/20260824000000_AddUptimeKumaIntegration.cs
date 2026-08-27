using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasAppManager.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260824000000_AddUptimeKumaIntegration")]
public sealed class AddUptimeKumaIntegration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "LastUptimeKumaError", table: "Settings", type: "TEXT", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "LastUptimeKumaSuccessUtc", table: "Settings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "LastUptimeKumaSyncUtc", table: "Settings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "UptimeKumaApiKeyEncrypted", table: "Settings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "UptimeKumaBaseUrl", table: "Settings", type: "TEXT", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<string>(name: "UptimeKumaBrowserUrl", table: "Settings", type: "TEXT", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "UptimeKumaEnabled", table: "Settings", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<int>(name: "UptimeKumaRefreshIntervalSeconds", table: "Settings", type: "INTEGER", nullable: false, defaultValue: 60);
        migrationBuilder.AddColumn<bool>(name: "UptimeKumaVerifyTls", table: "Settings", type: "INTEGER", nullable: false, defaultValue: true);

        migrationBuilder.CreateTable(
            name: "UptimeKumaMonitors",
            columns: table => new
            {
                MonitorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                Hostname = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Port = table.Column<int>(type: "INTEGER", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                ResponseTimeMilliseconds = table.Column<double>(type: "REAL", nullable: true),
                UptimeRatio1Day = table.Column<double>(type: "REAL", nullable: true),
                UptimeRatio30Days = table.Column<double>(type: "REAL", nullable: true),
                UptimeRatio365Days = table.Column<double>(type: "REAL", nullable: true),
                AverageResponseTimeMilliseconds1Day = table.Column<double>(type: "REAL", nullable: true),
                AverageResponseTimeMilliseconds30Days = table.Column<double>(type: "REAL", nullable: true),
                AverageResponseTimeMilliseconds365Days = table.Column<double>(type: "REAL", nullable: true),
                CertificateIsValid = table.Column<bool>(type: "INTEGER", nullable: true),
                CertificateDaysRemaining = table.Column<double>(type: "REAL", nullable: true),
                IsPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UptimeKumaMonitors", item => item.MonitorId);
                table.ForeignKey(name: "FK_UptimeKumaMonitors_Apps_AppId", column: item => item.AppId, principalTable: "Apps", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(name: "IX_UptimeKumaMonitors_AppId", table: "UptimeKumaMonitors", column: "AppId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UptimeKumaMonitors");
        migrationBuilder.DropColumn(name: "LastUptimeKumaError", table: "Settings");
        migrationBuilder.DropColumn(name: "LastUptimeKumaSuccessUtc", table: "Settings");
        migrationBuilder.DropColumn(name: "LastUptimeKumaSyncUtc", table: "Settings");
        migrationBuilder.DropColumn(name: "UptimeKumaApiKeyEncrypted", table: "Settings");
        migrationBuilder.DropColumn(name: "UptimeKumaBaseUrl", table: "Settings");
        migrationBuilder.DropColumn(name: "UptimeKumaBrowserUrl", table: "Settings");
        migrationBuilder.DropColumn(name: "UptimeKumaEnabled", table: "Settings");
        migrationBuilder.DropColumn(name: "UptimeKumaRefreshIntervalSeconds", table: "Settings");
        migrationBuilder.DropColumn(name: "UptimeKumaVerifyTls", table: "Settings");
    }
}
