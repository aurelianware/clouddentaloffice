using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddStripePatientCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatientPaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    PatientAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Selection = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PaymentReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ConnectedAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientPaymentAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientPaymentAttempts_PatientAccounts_TenantId_PatientAccountId",
                        columns: x => new { x.TenantId, x.PatientAccountId },
                        principalTable: "PatientAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientPaymentAttempts_PatientPayments_TenantId_PaymentId",
                        columns: x => new { x.TenantId, x.PaymentId },
                        principalTable: "PatientPayments",
                        principalColumns: new[] { "TenantId", "PaymentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientPaymentAttempts_PatientStatements_TenantId_StatementId",
                        columns: x => new { x.TenantId, x.StatementId },
                        principalTable: "PatientStatements",
                        principalColumns: new[] { "TenantId", "StatementId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId",
                table: "PatientPaymentAttempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId_PatientAccountId_CreatedAt",
                table: "PatientPaymentAttempts",
                columns: new[] { "TenantId", "PatientAccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId_PaymentId",
                table: "PatientPaymentAttempts",
                columns: new[] { "TenantId", "PaymentId" },
                unique: true,
                filter: "\"PaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId_PaymentReference",
                table: "PatientPaymentAttempts",
                columns: new[] { "TenantId", "PaymentReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId_StatementId",
                table: "PatientPaymentAttempts",
                columns: new[] { "TenantId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId_StripeCheckoutSessionId",
                table: "PatientPaymentAttempts",
                columns: new[] { "TenantId", "StripeCheckoutSessionId" },
                unique: true,
                filter: "\"StripeCheckoutSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientPaymentAttempts_TenantId_StripePaymentIntentId",
                table: "PatientPaymentAttempts",
                columns: new[] { "TenantId", "StripePaymentIntentId" },
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientPaymentAttempts");
        }
    }
}
