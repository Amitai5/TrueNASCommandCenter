using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasAppManager.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260827190000_AddWebPushNotifications")]
public sealed class AddWebPushNotifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "WebPushPrivateKeyEncrypted", table: "Settings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "WebPushPublicKey", table: "Settings", type: "TEXT", maxLength: 256, nullable: true);

        migrationBuilder.CreateTable(
            name: "WebPushSubscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Endpoint = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                P256dh = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Auth = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ExpirationUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastFailureUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_WebPushSubscriptions", item => item.Id));

        migrationBuilder.CreateIndex(
            name: "IX_WebPushSubscriptions_Endpoint",
            table: "WebPushSubscriptions",
            column: "Endpoint",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "WebPushSubscriptions");
        migrationBuilder.DropColumn(name: "WebPushPrivateKeyEncrypted", table: "Settings");
        migrationBuilder.DropColumn(name: "WebPushPublicKey", table: "Settings");
    }
}
