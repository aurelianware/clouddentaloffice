using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddActivePatientCoverageSlotIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PatientInsurances_TenantId_PatientId_SequenceNumber_Active",
                table: "PatientInsurances",
                columns: new[] { "TenantId", "PatientId", "SequenceNumber" },
                unique: true,
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PatientInsurances_TenantId_PatientId_SequenceNumber_Active",
                table: "PatientInsurances");
        }
    }
}
