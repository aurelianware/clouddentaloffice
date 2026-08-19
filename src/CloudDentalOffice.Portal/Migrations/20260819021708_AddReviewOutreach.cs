using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewOutreach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewOutreaches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Campaign = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LockId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewOutreaches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReviewOutreachSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DelayMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewLandingPageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    GoogleReviewUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SenderName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewOutreachSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewOutreaches_Status_ScheduledAt_NextAttemptAt",
                table: "ReviewOutreaches",
                columns: new[] { "Status", "ScheduledAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewOutreaches_TenantId",
                table: "ReviewOutreaches",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewOutreaches_TenantId_AppointmentId_Campaign",
                table: "ReviewOutreaches",
                columns: new[] { "TenantId", "AppointmentId", "Campaign" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewOutreachSettings_TenantId",
                table: "ReviewOutreachSettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewOutreaches");

            migrationBuilder.DropTable(
                name: "ReviewOutreachSettings");
        }
    }
}
