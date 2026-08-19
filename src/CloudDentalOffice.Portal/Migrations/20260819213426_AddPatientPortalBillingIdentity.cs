using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientPortalBillingIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Patients_TenantId_PatientId",
                table: "Patients",
                columns: new[] { "TenantId", "PatientId" });

            migrationBuilder.CreateTable(
                name: "PatientPortalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientPortalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientPortalIdentities_Patients_TenantId_PatientId",
                        columns: x => new { x.TenantId, x.PatientId },
                        principalTable: "Patients",
                        principalColumns: new[] { "TenantId", "PatientId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPortalIdentities_TenantId",
                table: "PatientPortalIdentities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPortalIdentities_TenantId_Issuer_Subject",
                table: "PatientPortalIdentities",
                columns: new[] { "TenantId", "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientPortalIdentities_TenantId_PatientId_IsActive",
                table: "PatientPortalIdentities",
                columns: new[] { "TenantId", "PatientId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientPortalIdentities");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Patients_TenantId_PatientId",
                table: "Patients");
        }
    }
}
