using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddEligibilityVerifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastVerificationState",
                table: "PatientInsurances",
                type: "TEXT",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastVerifiedAt",
                table: "PatientInsurances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EligibilityVerifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PatientInsuranceId = table.Column<int>(type: "INTEGER", nullable: false),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EligibilityVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EligibilityVerifications_PatientInsurances_PatientInsuranceId",
                        column: x => x.PatientInsuranceId,
                        principalTable: "PatientInsurances",
                        principalColumn: "PatientInsuranceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EligibilityVerifications_PatientInsuranceId",
                table: "EligibilityVerifications",
                column: "PatientInsuranceId");

            migrationBuilder.CreateIndex(
                name: "IX_EligibilityVerifications_TenantId_PatientInsuranceId_CheckedAt",
                table: "EligibilityVerifications",
                columns: new[] { "TenantId", "PatientInsuranceId", "CheckedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EligibilityVerifications");

            migrationBuilder.DropColumn(
                name: "LastVerificationState",
                table: "PatientInsurances");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                table: "PatientInsurances");
        }
    }
}
