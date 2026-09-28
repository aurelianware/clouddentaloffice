using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddCoverageIntakeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverageIntakeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverageVerificationId = table.Column<long>(type: "INTEGER", nullable: false),
                    RecipientEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SendAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AnsweredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Answer = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CarrierName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    MemberId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    GroupNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RelationshipToSubscriber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    SubscriberFirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SubscriberLastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SubscriberDateOfBirth = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverageIntakeRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageIntakeRequests_TenantId_PatientId_CreatedAt",
                table: "CoverageIntakeRequests",
                columns: new[] { "TenantId", "PatientId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverageIntakeRequests");
        }
    }
}
