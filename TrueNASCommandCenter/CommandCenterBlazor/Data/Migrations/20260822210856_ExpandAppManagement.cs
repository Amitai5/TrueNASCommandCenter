using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasCommandCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpandAppManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GitHubEnrichmentEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastInventoryRefreshUtc",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortalHostOverride",
                table: "Settings",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Apps",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DowntimeAction",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "Ignore");

            migrationBuilder.AddColumn<Guid>(
                name: "HealthIncidentId",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthMessage",
                table: "Apps",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthState",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "HomeUrl",
                table: "Apps",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IconUrl",
                table: "Apps",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInstalled",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHealthCheckUtc",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceMode",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ManualPortalUrl",
                table: "Apps",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MissingSinceUtc",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryAttemptedUtc",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrlsJson",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Train",
                table: "Apps",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppContainers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Image = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NetworksJson = table.Column<string>(type: "TEXT", nullable: true),
                    VolumesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppContainers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppContainers_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppPortals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppPortals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppPortals_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppPorts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    HostIp = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    HostPort = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerPort = table.Column<int>(type: "INTEGER", nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppPorts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppPorts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitHubRepositories",
                columns: table => new
                {
                    RepositoryUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    License = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Stars = table.Column<int>(type: "INTEGER", nullable: true),
                    TopicsJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastFetchedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubRepositories", x => x.RepositoryUrl);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppContainers_AppId_ContainerId",
                table: "AppContainers",
                columns: new[] { "AppId", "ContainerId" });

            migrationBuilder.CreateIndex(
                name: "IX_AppPortals_AppId",
                table: "AppPortals",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppPorts_AppId_HostPort_Protocol",
                table: "AppPorts",
                columns: new[] { "AppId", "HostPort", "Protocol" });

            migrationBuilder.Sql("UPDATE Apps SET DowntimeAction = CASE WHEN NotifyOnDowntime = 1 THEN 'NotifyOnly' ELSE 'Ignore' END;");
            migrationBuilder.Sql("UPDATE Apps SET HealthState = CASE WHEN State = 'RUNNING' THEN 'Running' WHEN State IN ('STOPPED', 'CRASHED') THEN 'Stopped' ELSE 'Unknown' END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppContainers");

            migrationBuilder.DropTable(
                name: "AppPortals");

            migrationBuilder.DropTable(
                name: "AppPorts");

            migrationBuilder.DropTable(
                name: "GitHubRepositories");

            migrationBuilder.DropColumn(
                name: "GitHubEnrichmentEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LastInventoryRefreshUtc",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PortalHostOverride",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "DowntimeAction",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "HealthIncidentId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "HealthMessage",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "HealthState",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "HomeUrl",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "IconUrl",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "IsInstalled",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "LastHealthCheckUtc",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "MaintenanceMode",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ManualPortalUrl",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "MissingSinceUtc",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RecoveryAttemptedUtc",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "SourceUrlsJson",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Train",
                table: "Apps");
        }
    }
}
