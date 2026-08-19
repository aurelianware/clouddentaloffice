using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffPatientBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "PatientPayments",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "system:migration");

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalLedgerEntryId",
                table: "PatientPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "PatientPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversedBy",
                table: "PatientPayments",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnappliedAt",
                table: "PatientPaymentAllocations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnappliedBy",
                table: "PatientPaymentAllocations",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnapplyReasonCode",
                table: "PatientPaymentAllocations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FinancialAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId_ReversalLedgerEntryId",
                table: "PatientPayments",
                columns: new[] { "TenantId", "ReversalLedgerEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAuditEvents_TenantId",
                table: "FinancialAuditEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAuditEvents_TenantId_EntityType_EntityId_CreatedAt",
                table: "FinancialAuditEvents",
                columns: new[] { "TenantId", "EntityType", "EntityId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_PatientPayments_PatientLedgerEntries_TenantId_ReversalLedgerEntryId",
                table: "PatientPayments",
                columns: new[] { "TenantId", "ReversalLedgerEntryId" },
                principalTable: "PatientLedgerEntries",
                principalColumns: new[] { "TenantId", "LedgerEntryId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PatientPayments_PatientLedgerEntries_TenantId_ReversalLedgerEntryId",
                table: "PatientPayments");

            migrationBuilder.DropTable(
                name: "FinancialAuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_PatientPayments_TenantId_ReversalLedgerEntryId",
                table: "PatientPayments");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "PatientPayments");

            migrationBuilder.DropColumn(
                name: "ReversalLedgerEntryId",
                table: "PatientPayments");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "PatientPayments");

            migrationBuilder.DropColumn(
                name: "ReversedBy",
                table: "PatientPayments");

            migrationBuilder.DropColumn(
                name: "UnappliedAt",
                table: "PatientPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "UnappliedBy",
                table: "PatientPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "UnapplyReasonCode",
                table: "PatientPaymentAllocations");
        }
    }
}
