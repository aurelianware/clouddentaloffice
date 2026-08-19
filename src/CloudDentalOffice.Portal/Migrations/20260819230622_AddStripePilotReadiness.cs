using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddStripePilotReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReconciliationAt",
                table: "PaymentProcessorConfigurations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastReconciliationStatusCode",
                table: "PaymentProcessorConfigurations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PatientBillingNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NotificationType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecipientEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PracticeName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
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
                    table.PrimaryKey("PK_PatientBillingNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientBillingNotifications_PatientAccounts_TenantId_PatientAccountId",
                        columns: x => new { x.TenantId, x.PatientAccountId },
                        principalTable: "PatientAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientBillingNotifications_Status_ScheduledAt_NextAttemptAt",
                table: "PatientBillingNotifications",
                columns: new[] { "Status", "ScheduledAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientBillingNotifications_TenantId",
                table: "PatientBillingNotifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientBillingNotifications_TenantId_NotificationType_SourceType_SourceId",
                table: "PatientBillingNotifications",
                columns: new[] { "TenantId", "NotificationType", "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientBillingNotifications_TenantId_PatientAccountId",
                table: "PatientBillingNotifications",
                columns: new[] { "TenantId", "PatientAccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientBillingNotifications");

            migrationBuilder.DropColumn(
                name: "LastReconciliationAt",
                table: "PaymentProcessorConfigurations");

            migrationBuilder.DropColumn(
                name: "LastReconciliationStatusCode",
                table: "PaymentProcessorConfigurations");
        }
    }
}
