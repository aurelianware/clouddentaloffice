using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloudHealthOfficeClaimId",
                table: "Claims",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleStatus",
                table: "Claims",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastIntelligenceAt",
                table: "Claims",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinancialsPostedAt",
                table: "Claims",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claims_TenantId_CloudHealthOfficeClaimId",
                table: "Claims",
                columns: new[] { "TenantId", "CloudHealthOfficeClaimId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Claims_TenantId_CloudHealthOfficeClaimId",
                table: "Claims");

            migrationBuilder.DropColumn(
                name: "CloudHealthOfficeClaimId",
                table: "Claims");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "Claims");

            migrationBuilder.DropColumn(
                name: "LastIntelligenceAt",
                table: "Claims");

            migrationBuilder.DropColumn(
                name: "FinancialsPostedAt",
                table: "Claims");
        }
    }
}
