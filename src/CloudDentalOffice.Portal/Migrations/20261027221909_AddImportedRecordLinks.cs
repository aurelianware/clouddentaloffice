using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedRecordLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedRecordLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "demo"),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LocalId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedRecordLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedRecordLinks_TenantId",
                table: "ImportedRecordLinks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedRecordLinks_TenantId_Source_EntityType_ExternalId",
                table: "ImportedRecordLinks",
                columns: new[] { "TenantId", "Source", "EntityType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedRecordLinks_TenantId_Source_EntityType_LocalId",
                table: "ImportedRecordLinks",
                columns: new[] { "TenantId", "Source", "EntityType", "LocalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedRecordLinks");
        }
    }
}
