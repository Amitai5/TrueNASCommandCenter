using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueNasAppManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Apps",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsCustom = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    InstalledVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    HumanVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LatestVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LatestHumanVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CatalogUpdateAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImageUpdateAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    OutdatedImagesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ActionRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessfulUpdateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Policy = table.Column<string>(type: "TEXT", nullable: true),
                    VersionScope = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotHostPaths = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifySuccessOverride = table.Column<bool>(type: "INTEGER", nullable: true),
                    StatusLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Apps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OnboardingCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnboardingStep = table.Column<int>(type: "INTEGER", nullable: false),
                    TrueNasUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    TrueNasUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TrueNasApiKeyEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    VerifyTls = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowInsecureWebSocket = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastConnectionSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastConnectionErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastConnectionError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SchedulerEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastScheduledRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCompletedCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotifyManualApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyAutomaticFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyAutomaticBlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyRollback = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyAutomaticSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyScheduledCheckFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyConnectionFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: true),
                    SmtpSecurity = table.Column<string>(type: "TEXT", nullable: true),
                    SmtpUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SmtpPasswordEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    EmailFromName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailFromAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    EmailRecipientsJson = table.Column<string>(type: "TEXT", nullable: true),
                    WebhookEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebhookUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    WebhookAuthorizationEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    WebhookHeadersEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    WebhookTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    VerificationTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectionFailureCooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    HistoryRetentionDays = table.Column<int>(type: "INTEGER", nullable: true),
                    ManagerAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CheckedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EligibleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SucceededCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ToVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OutdatedImagesJson = table.Column<string>(type: "TEXT", nullable: true),
                    PolicyAtExecution = table.Column<string>(type: "TEXT", nullable: true),
                    ScopeAtExecution = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ReasonMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TrueNasJobId = table.Column<long>(type: "INTEGER", nullable: true),
                    TrueNasJobState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ErrorDetails = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpdateAttempts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpdateAttempts_UpdateRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "UpdateRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DeduplicationKey_Provider_Status",
                table: "Notifications",
                columns: new[] { "DeduplicationKey", "Provider", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateAttempts_AppId_StartedUtc",
                table: "UpdateAttempts",
                columns: new[] { "AppId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateAttempts_RunId",
                table: "UpdateAttempts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRuns_StartedUtc",
                table: "UpdateRuns",
                column: "StartedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "UpdateAttempts");

            migrationBuilder.DropTable(
                name: "Apps");

            migrationBuilder.DropTable(
                name: "UpdateRuns");
        }
    }
}
