using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasAppManager.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260823000000_AddLocalRemoteWebUiLinks")]
public sealed class AddLocalRemoteWebUiLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LocalPortalUrl",
            table: "Apps",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RemotePortalUrl",
            table: "Apps",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LocalPortalUrl", table: "Apps");
        migrationBuilder.DropColumn(name: "RemotePortalUrl", table: "Apps");
    }
}
