using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasCommandCenter.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260827000000_AddAppOrganization")]
public sealed class AddAppOrganization : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "GroupName", table: "Apps", type: "TEXT", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsFavorite", table: "Apps", type: "INTEGER", nullable: false, defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "GroupName", table: "Apps");
        migrationBuilder.DropColumn(name: "IsFavorite", table: "Apps");
    }
}
