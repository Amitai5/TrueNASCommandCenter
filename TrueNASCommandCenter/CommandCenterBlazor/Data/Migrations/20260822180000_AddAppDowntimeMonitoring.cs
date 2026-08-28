using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasCommandCenter.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260822180000_AddAppDowntimeMonitoring")]
public sealed class AddAppDowntimeMonitoring : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "DowntimeNotificationActive",
            table: "Apps",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "NotifyOnDowntime",
            table: "Apps",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DowntimeNotificationActive", table: "Apps");
        migrationBuilder.DropColumn(name: "NotifyOnDowntime", table: "Apps");
    }
}
