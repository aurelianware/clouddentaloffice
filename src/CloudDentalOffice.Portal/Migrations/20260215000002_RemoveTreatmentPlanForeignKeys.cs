using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTreatmentPlanForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop FK constraints from TreatmentPlans table (references to separate microservice databases)
            migrationBuilder.DropForeignKey(
                name: "FK_TreatmentPlans_Patients_PatientId",
                table: "TreatmentPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_TreatmentPlans_Providers_ProviderId",
                table: "TreatmentPlans");

            // Drop FK constraints from PlannedProcedures table
            migrationBuilder.DropForeignKey(
                name: "FK_PlannedProcedures_ClaimProcedures_ClaimProcedureId",
                table: "PlannedProcedures");

            // Drop existing indexes
            migrationBuilder.DropIndex(
                name: "IX_TreatmentPlans_PatientId",
                table: "TreatmentPlans");

            migrationBuilder.DropIndex(
                name: "IX_TreatmentPlans_ProviderId",
                table: "TreatmentPlans");

            migrationBuilder.DropIndex(
                name: "IX_PlannedProcedures_ClaimProcedureId",
                table: "PlannedProcedures");

            // Recreate indexes without FK constraints for query performance
            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPlans_PatientId",
                table: "TreatmentPlans",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPlans_ProviderId",
                table: "TreatmentPlans",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedProcedures_ClaimProcedureId",
                table: "PlannedProcedures",
                column: "ClaimProcedureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop non-FK indexes
            migrationBuilder.DropIndex(
                name: "IX_TreatmentPlans_PatientId",
                table: "TreatmentPlans");

            migrationBuilder.DropIndex(
                name: "IX_TreatmentPlans_ProviderId",
                table: "TreatmentPlans");

            migrationBuilder.DropIndex(
                name: "IX_PlannedProcedures_ClaimProcedureId",
                table: "PlannedProcedures");

            // Recreate FK indexes and constraints (for rollback)
            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPlans_PatientId",
                table: "TreatmentPlans",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPlans_ProviderId",
                table: "TreatmentPlans",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedProcedures_ClaimProcedureId",
                table: "PlannedProcedures",
                column: "ClaimProcedureId");

            migrationBuilder.AddForeignKey(
                name: "FK_TreatmentPlans_Patients_PatientId",
                table: "TreatmentPlans",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TreatmentPlans_Providers_ProviderId",
                table: "TreatmentPlans",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlannedProcedures_ClaimProcedures_ClaimProcedureId",
                table: "PlannedProcedures",
                column: "ClaimProcedureId",
                principalTable: "ClaimProcedures",
                principalColumn: "ClaimProcedureId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
