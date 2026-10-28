using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteInsuranceChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "CoverageVerificationId",
                table: "CoverageIntakeRequests",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.CreateTable(
                name: "WebsiteInsuranceChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Carrier = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Plan = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    RequestingAppointment = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Campaign = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AttributionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverageIntakeRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteInsuranceChecks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteInsuranceChecks_TenantId_ClosedAt_ReceivedAt",
                table: "WebsiteInsuranceChecks",
                columns: new[] { "TenantId", "ClosedAt", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebsiteInsuranceChecks");

            migrationBuilder.AlterColumn<long>(
                name: "CoverageVerificationId",
                table: "CoverageIntakeRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
