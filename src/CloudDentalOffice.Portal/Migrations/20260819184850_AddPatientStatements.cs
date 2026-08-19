using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PatientLedgerEntries_TenantId_LedgerEntryId",
                table: "PatientLedgerEntries",
                columns: new[] { "TenantId", "LedgerEntryId" });

            migrationBuilder.CreateTable(
                name: "PatientStatements",
                columns: table => new
                {
                    StatementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatementDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    BalanceForward = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewCharges = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    InsurancePayments = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Adjustments = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PatientPayments = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Credits = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Refunds = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DebitAdjustments = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountDue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    LedgerThroughDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StatusUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SupersedesStatementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SupersededByStatementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VoidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidReasonCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientStatements", x => x.StatementId);
                    table.UniqueConstraint("AK_PatientStatements_TenantId_StatementId", x => new { x.TenantId, x.StatementId });
                    table.ForeignKey(
                        name: "FK_PatientStatements_PatientAccounts_TenantId_PatientAccountId",
                        columns: x => new { x.TenantId, x.PatientAccountId },
                        principalTable: "PatientAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientStatementLines",
                columns: table => new
                {
                    StatementLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    StatementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActivityDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EntryType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PatientDescription = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientStatementLines", x => x.StatementLineId);
                    table.ForeignKey(
                        name: "FK_PatientStatementLines_PatientLedgerEntries_TenantId_LedgerEntryId",
                        columns: x => new { x.TenantId, x.LedgerEntryId },
                        principalTable: "PatientLedgerEntries",
                        principalColumns: new[] { "TenantId", "LedgerEntryId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientStatementLines_PatientStatements_TenantId_StatementId",
                        columns: x => new { x.TenantId, x.StatementId },
                        principalTable: "PatientStatements",
                        principalColumns: new[] { "TenantId", "StatementId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientStatementLines_TenantId",
                table: "PatientStatementLines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientStatementLines_TenantId_LedgerEntryId",
                table: "PatientStatementLines",
                columns: new[] { "TenantId", "LedgerEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientStatementLines_TenantId_StatementId_LedgerEntryId",
                table: "PatientStatementLines",
                columns: new[] { "TenantId", "StatementId", "LedgerEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientStatements_TenantId",
                table: "PatientStatements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientStatements_TenantId_PatientAccountId_LedgerThroughDate",
                table: "PatientStatements",
                columns: new[] { "TenantId", "PatientAccountId", "LedgerThroughDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientStatements_TenantId_PatientAccountId_StatementDate",
                table: "PatientStatements",
                columns: new[] { "TenantId", "PatientAccountId", "StatementDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientStatementLines");

            migrationBuilder.DropTable(
                name: "PatientStatements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PatientLedgerEntries_TenantId_LedgerEntryId",
                table: "PatientLedgerEntries");
        }
    }
}
