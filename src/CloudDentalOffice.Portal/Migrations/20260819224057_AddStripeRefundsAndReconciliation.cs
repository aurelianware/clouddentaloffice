using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeRefundsAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PatientPaymentAllocations_TenantId_PaymentId_LedgerEntryId",
                table: "PatientPaymentAllocations");

            migrationBuilder.CreateTable(
                name: "PatientRefunds",
                columns: table => new
                {
                    RefundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Processor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    InternalRefundReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalRefundId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientRefunds", x => x.RefundId);
                    table.UniqueConstraint("AK_PatientRefunds_TenantId_RefundId", x => new { x.TenantId, x.RefundId });
                    table.ForeignKey(
                        name: "FK_PatientRefunds_PatientLedgerEntries_TenantId_LedgerEntryId",
                        columns: x => new { x.TenantId, x.LedgerEntryId },
                        principalTable: "PatientLedgerEntries",
                        principalColumns: new[] { "TenantId", "LedgerEntryId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientRefunds_PatientPayments_TenantId_PaymentId",
                        columns: x => new { x.TenantId, x.PaymentId },
                        principalTable: "PatientPayments",
                        principalColumns: new[] { "TenantId", "PaymentId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentReconciliationIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    IssueType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RefundId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExternalReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DiagnosticCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReconciliationIssues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAllocations_TenantId_PaymentId_LedgerEntryId",
                table: "PatientPaymentAllocations",
                columns: new[] { "TenantId", "PaymentId", "LedgerEntryId" },
                unique: true,
                filter: "\"UnappliedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientRefunds_TenantId",
                table: "PatientRefunds",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientRefunds_TenantId_InternalRefundReference",
                table: "PatientRefunds",
                columns: new[] { "TenantId", "InternalRefundReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientRefunds_TenantId_LedgerEntryId",
                table: "PatientRefunds",
                columns: new[] { "TenantId", "LedgerEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientRefunds_TenantId_PaymentId_Status",
                table: "PatientRefunds",
                columns: new[] { "TenantId", "PaymentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientRefunds_TenantId_Processor_ExternalRefundId",
                table: "PatientRefunds",
                columns: new[] { "TenantId", "Processor", "ExternalRefundId" },
                unique: true,
                filter: "\"ExternalRefundId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReconciliationIssues_TenantId",
                table: "PaymentReconciliationIssues",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReconciliationIssues_TenantId_IssueType_ExternalReference_Status",
                table: "PaymentReconciliationIssues",
                columns: new[] { "TenantId", "IssueType", "ExternalReference", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReconciliationIssues_TenantId_Status_DetectedAt",
                table: "PaymentReconciliationIssues",
                columns: new[] { "TenantId", "Status", "DetectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientRefunds");

            migrationBuilder.DropTable(
                name: "PaymentReconciliationIssues");

            migrationBuilder.DropIndex(
                name: "IX_PatientPaymentAllocations_TenantId_PaymentId_LedgerEntryId",
                table: "PatientPaymentAllocations");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAllocations_TenantId_PaymentId_LedgerEntryId",
                table: "PatientPaymentAllocations",
                columns: new[] { "TenantId", "PaymentId", "LedgerEntryId" },
                unique: true);
        }
    }
}
