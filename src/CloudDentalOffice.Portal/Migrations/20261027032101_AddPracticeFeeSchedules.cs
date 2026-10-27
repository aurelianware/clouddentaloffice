using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeFeeSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PracticeFeeSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsPracticeDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    EntriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeFeeSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeSchedules_TenantId",
                table: "PracticeFeeSchedules",
                column: "TenantId",
                unique: true,
                filter: "\"IsPracticeDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeSchedules_TenantId_Name",
                table: "PracticeFeeSchedules",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PracticeFeeSchedules");
        }
    }
}
