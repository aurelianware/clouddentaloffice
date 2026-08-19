using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProcessingBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatientPayments",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PaymentDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Processor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExternalPaymentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InternalPaymentReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientPayments", x => x.PaymentId);
                    table.UniqueConstraint("AK_PatientPayments_TenantId_PaymentId", x => new { x.TenantId, x.PaymentId });
                    table.ForeignKey(
                        name: "FK_PatientPayments_PatientAccounts_TenantId_PatientAccountId",
                        columns: x => new { x.TenantId, x.PatientAccountId },
                        principalTable: "PatientAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientPayments_PatientLedgerEntries_TenantId_LedgerEntryId",
                        columns: x => new { x.TenantId, x.LedgerEntryId },
                        principalTable: "PatientLedgerEntries",
                        principalColumns: new[] { "TenantId", "LedgerEntryId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientPayments_PatientStatements_TenantId_StatementId",
                        columns: x => new { x.TenantId, x.StatementId },
                        principalTable: "PatientStatements",
                        principalColumns: new[] { "TenantId", "StatementId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProcessorConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CredentialReference = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConnectedMerchantReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProcessorConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatientPaymentAllocations",
                columns: table => new
                {
                    PaymentAllocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientPaymentAllocations", x => x.PaymentAllocationId);
                    table.ForeignKey(
                        name: "FK_PatientPaymentAllocations_PatientLedgerEntries_TenantId_LedgerEntryId",
                        columns: x => new { x.TenantId, x.LedgerEntryId },
                        principalTable: "PatientLedgerEntries",
                        principalColumns: new[] { "TenantId", "LedgerEntryId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientPaymentAllocations_PatientPayments_TenantId_PaymentId",
                        columns: x => new { x.TenantId, x.PaymentId },
                        principalTable: "PatientPayments",
                        principalColumns: new[] { "TenantId", "PaymentId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProcessorEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    Processor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalEventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalPaymentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProcessorEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentProcessorEvents_PatientPayments_TenantId_PaymentId",
                        columns: x => new { x.TenantId, x.PaymentId },
                        principalTable: "PatientPayments",
                        principalColumns: new[] { "TenantId", "PaymentId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAllocations_TenantId",
                table: "PatientPaymentAllocations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAllocations_TenantId_LedgerEntryId",
                table: "PatientPaymentAllocations",
                columns: new[] { "TenantId", "LedgerEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAllocations_TenantId_PaymentId_LedgerEntryId",
                table: "PatientPaymentAllocations",
                columns: new[] { "TenantId", "PaymentId", "LedgerEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId",
                table: "PatientPayments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId_InternalPaymentReference",
                table: "PatientPayments",
                columns: new[] { "TenantId", "InternalPaymentReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId_LedgerEntryId",
                table: "PatientPayments",
                columns: new[] { "TenantId", "LedgerEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId_PatientAccountId_PaymentDate",
                table: "PatientPayments",
                columns: new[] { "TenantId", "PatientAccountId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId_Processor_ExternalPaymentId",
                table: "PatientPayments",
                columns: new[] { "TenantId", "Processor", "ExternalPaymentId" },
                unique: true,
                filter: "\"ExternalPaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPayments_TenantId_StatementId",
                table: "PatientPayments",
                columns: new[] { "TenantId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProcessorConfigurations_TenantId",
                table: "PaymentProcessorConfigurations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProcessorConfigurations_TenantId_Provider",
                table: "PaymentProcessorConfigurations",
                columns: new[] { "TenantId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProcessorEvents_TenantId",
                table: "PaymentProcessorEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProcessorEvents_TenantId_PaymentId_CreatedAt",
                table: "PaymentProcessorEvents",
                columns: new[] { "TenantId", "PaymentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProcessorEvents_TenantId_Processor_ExternalEventId",
                table: "PaymentProcessorEvents",
                columns: new[] { "TenantId", "Processor", "ExternalEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientPaymentAllocations");

            migrationBuilder.DropTable(
                name: "PaymentProcessorConfigurations");

            migrationBuilder.DropTable(
                name: "PaymentProcessorEvents");

            migrationBuilder.DropTable(
                name: "PatientPayments");
        }
    }
}
