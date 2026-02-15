using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProceduresClinicalNotesForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop FK constraints from Procedures table (references to separate microservice databases)
            migrationBuilder.DropForeignKey(
                name: "FK_Procedures_Patients_PatientId",
                table: "Procedures");

            migrationBuilder.DropForeignKey(
                name: "FK_Procedures_Providers_ProviderId",
                table: "Procedures");

            migrationBuilder.DropForeignKey(
                name: "FK_Procedures_Appointments_AppointmentId",
                table: "Procedures");

            // Drop FK constraints from ClinicalNotes table
            migrationBuilder.DropForeignKey(
                name: "FK_ClinicalNotes_Patients_PatientId",
                table: "ClinicalNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_ClinicalNotes_Providers_ProviderId",
                table: "ClinicalNotes");

            // Drop existing indexes
            migrationBuilder.DropIndex(
                name: "IX_Procedures_PatientId",
                table: "Procedures");

            migrationBuilder.DropIndex(
                name: "IX_Procedures_ProviderId",
                table: "Procedures");

            migrationBuilder.DropIndex(
                name: "IX_Procedures_AppointmentId",
                table: "Procedures");

            migrationBuilder.DropIndex(
                name: "IX_ClinicalNotes_PatientId",
                table: "ClinicalNotes");

            migrationBuilder.DropIndex(
                name: "IX_ClinicalNotes_ProviderId",
                table: "ClinicalNotes");

            // Recreate indexes without FK constraints for query performance
            migrationBuilder.CreateIndex(
                name: "IX_Procedures_PatientId",
                table: "Procedures",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Procedures_ProviderId",
                table: "Procedures",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Procedures_AppointmentId",
                table: "Procedures",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_PatientId",
                table: "ClinicalNotes",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_ProviderId",
                table: "ClinicalNotes",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop non-FK indexes
            migrationBuilder.DropIndex(
                name: "IX_Procedures_PatientId",
                table: "Procedures");

            migrationBuilder.DropIndex(
                name: "IX_Procedures_ProviderId",
                table: "Procedures");

            migrationBuilder.DropIndex(
                name: "IX_Procedures_AppointmentId",
                table: "Procedures");

            migrationBuilder.DropIndex(
                name: "IX_ClinicalNotes_PatientId",
                table: "ClinicalNotes");

            migrationBuilder.DropIndex(
                name: "IX_ClinicalNotes_ProviderId",
                table: "ClinicalNotes");

            // Recreate FK indexes and constraints (for rollback)
            migrationBuilder.CreateIndex(
                name: "IX_Procedures_PatientId",
                table: "Procedures",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Procedures_ProviderId",
                table: "Procedures",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Procedures_AppointmentId",
                table: "Procedures",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_PatientId",
                table: "ClinicalNotes",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_ProviderId",
                table: "ClinicalNotes",
                column: "ProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Procedures_Patients_PatientId",
                table: "Procedures",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Procedures_Providers_ProviderId",
                table: "Procedures",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Procedures_Appointments_AppointmentId",
                table: "Procedures",
                column: "AppointmentId",
                principalTable: "Appointments",
                principalColumn: "AppointmentId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicalNotes_Patients_PatientId",
                table: "ClinicalNotes",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicalNotes_Providers_ProviderId",
                table: "ClinicalNotes",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
