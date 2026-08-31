using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasCommandCenter.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260830000000_AddOperationsInbox")]
public sealed class AddOperationsInbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OperationsInboxItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Fingerprint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                CorrelationGroup = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                Severity = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Summary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                Details = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                SourceReference = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                RelatedAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                DeepLink = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                AcknowledgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                IsSourceActive = table.Column<bool>(type: "INTEGER", nullable: false),
                ProgressPercent = table.Column<double>(type: "REAL", nullable: true),
                OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                PushState = table.Column<string>(type: "TEXT", nullable: false),
                PushAttemptedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                PushError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_OperationsInboxItems", item => item.Id));

        migrationBuilder.CreateTable(
            name: "OperationsInboxHistory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                InboxItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Action = table.Column<string>(type: "TEXT", nullable: false),
                TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationsInboxHistory", item => item.Id);
                table.ForeignKey(
                    name: "FK_OperationsInboxHistory_OperationsInboxItems_InboxItemId",
                    column: item => item.InboxItemId,
                    principalTable: "OperationsInboxItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_OperationsInboxItems_Fingerprint", table: "OperationsInboxItems", column: "Fingerprint", unique: true);
        migrationBuilder.CreateIndex(name: "IX_OperationsInboxItems_Status_Severity_OccurredUtc", table: "OperationsInboxItems", columns: new[] { "Status", "Severity", "OccurredUtc" });
        migrationBuilder.CreateIndex(name: "IX_OperationsInboxHistory_InboxItemId_TimestampUtc", table: "OperationsInboxHistory", columns: new[] { "InboxItemId", "TimestampUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OperationsInboxHistory");
        migrationBuilder.DropTable(name: "OperationsInboxItems");
    }
}
