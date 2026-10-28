using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeBillingProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PracticeBillingProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LegalName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Npi = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    TaxId = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    TaxonomyCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Address1 = table.Column<string>(type: "TEXT", maxLength: 55, nullable: false),
                    Address2 = table.Column<string>(type: "TEXT", maxLength: 55, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    ZipCode = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    ContactName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeBillingProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeBillingProfiles_TenantId",
                table: "PracticeBillingProfiles",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PracticeBillingProfiles");
        }
    }
}
