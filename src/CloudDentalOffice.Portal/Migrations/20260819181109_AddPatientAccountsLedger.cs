using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientAccountsLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatientAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientAccounts", x => x.Id);
                    table.UniqueConstraint("AK_PatientAccounts_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "PatientLedgerEntries",
                columns: table => new
                {
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DescriptionCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReversalOfEntryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientLedgerEntries", x => x.LedgerEntryId);
                    table.ForeignKey(
                        name: "FK_PatientLedgerEntries_PatientAccounts_TenantId_PatientAccountId",
                        columns: x => new { x.TenantId, x.PatientAccountId },
                        principalTable: "PatientAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientLedgerEntries_PatientLedgerEntries_ReversalOfEntryId",
                        column: x => x.ReversalOfEntryId,
                        principalTable: "PatientLedgerEntries",
                        principalColumn: "LedgerEntryId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientAccounts_TenantId",
                table: "PatientAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientAccounts_TenantId_PatientId",
                table: "PatientAccounts",
                columns: new[] { "TenantId", "PatientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientLedgerEntries_ReversalOfEntryId",
                table: "PatientLedgerEntries",
                column: "ReversalOfEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientLedgerEntries_TenantId",
                table: "PatientLedgerEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientLedgerEntries_TenantId_PatientAccountId_EffectiveDate",
                table: "PatientLedgerEntries",
                columns: new[] { "TenantId", "PatientAccountId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientLedgerEntries_TenantId_ReversalOfEntryId",
                table: "PatientLedgerEntries",
                columns: new[] { "TenantId", "ReversalOfEntryId" },
                unique: true,
                filter: "\"ReversalOfEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientLedgerEntries_TenantId_SourceType_SourceId_EntryType",
                table: "PatientLedgerEntries",
                columns: new[] { "TenantId", "SourceType", "SourceId", "EntryType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientLedgerEntries");

            migrationBuilder.DropTable(
                name: "PatientAccounts");
        }
    }
}
