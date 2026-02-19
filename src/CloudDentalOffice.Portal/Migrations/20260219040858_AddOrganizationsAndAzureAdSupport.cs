using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationsAndAzureAdSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_Providers_ProviderId",
                table: "Appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_Claims_Patients_PatientId",
                table: "Claims");

            migrationBuilder.DropForeignKey(
                name: "FK_Claims_Providers_ProviderId",
                table: "Claims");

            migrationBuilder.DropForeignKey(
                name: "FK_ClinicalNotes_Patients_PatientId",
                table: "ClinicalNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_ClinicalNotes_Providers_ProviderId",
                table: "ClinicalNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_PlannedProcedures_ClaimProcedures_ClaimProcedureId",
                table: "PlannedProcedures");

            migrationBuilder.DropForeignKey(
                name: "FK_Procedures_Appointments_AppointmentId",
                table: "Procedures");

            migrationBuilder.DropForeignKey(
                name: "FK_Procedures_Patients_PatientId",
                table: "Procedures");

            migrationBuilder.DropForeignKey(
                name: "FK_Procedures_Providers_ProviderId",
                table: "Procedures");

            migrationBuilder.DropForeignKey(
                name: "FK_TreatmentPlans_Providers_ProviderId",
                table: "TreatmentPlans");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "AzureAdObjectId",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureAdUpn",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanInviteUsers",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "TreatmentPlans",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Providers",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Procedures",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "PlannedProcedures",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Patients",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "PatientInsurances",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "InsurancePlans",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "ClinicalNotes",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Claims",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "ClaimProcedures",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Appointments",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "demo",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "dev");

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    AzureAdTenantId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Domain = table.Column<string>(type: "TEXT", nullable: true),
                    Plan = table.Column<string>(type: "TEXT", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "TEXT", nullable: true),
                    StripeSubscriptionId = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrialExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Settings = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 1,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6040));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 2,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6040));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 3,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6040));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 4,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6040));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 5,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6050));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 6,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6050));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 7,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6050));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 8,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6050));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 9,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6050));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 10,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6050));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 11,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6060));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 12,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6060));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 13,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6060));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 14,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6060));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 15,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6060));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 16,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6060));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 17,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6070));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 18,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6070));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 19,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6070));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 20,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6070));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 21,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6070));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 22,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6070));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 23,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 24,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 25,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 26,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 27,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 28,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 29,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6080));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 30,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6090));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 31,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6090));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 32,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6090));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 33,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6110));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 34,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6120));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 35,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6120));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 36,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6120));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 37,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6120));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 38,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6120));

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 1,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6230), "demo" });

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 2,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6240), "demo" });

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 3,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6240), "demo" });

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 4,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 19, 4, 8, 57, 710, DateTimeKind.Utc).AddTicks(6240), "demo" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_OrganizationId",
                table: "Users",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_Providers_ProviderId",
                table: "Appointments",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Claims_Patients_PatientId",
                table: "Claims",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Claims_Providers_ProviderId",
                table: "Claims",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TreatmentPlans_Providers_ProviderId",
                table: "TreatmentPlans",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Organizations_OrganizationId",
                table: "Users",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_Providers_ProviderId",
                table: "Appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_Claims_Patients_PatientId",
                table: "Claims");

            migrationBuilder.DropForeignKey(
                name: "FK_Claims_Providers_ProviderId",
                table: "Claims");

            migrationBuilder.DropForeignKey(
                name: "FK_TreatmentPlans_Providers_ProviderId",
                table: "TreatmentPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Organizations_OrganizationId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Users_OrganizationId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AzureAdObjectId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AzureAdUpn",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanInviteUsers",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "TreatmentPlans",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Providers",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Procedures",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "PlannedProcedures",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Patients",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "PatientInsurances",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "InsurancePlans",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "ClinicalNotes",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Claims",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "ClaimProcedures",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "Appointments",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "dev",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldDefaultValue: "demo");

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 1,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(950));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 2,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(950));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 3,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(950));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 4,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(950));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 5,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(950));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 6,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(960));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 7,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(960));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 8,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(960));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 9,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(960));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 10,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(960));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 11,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(960));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 12,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(970));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 13,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(970));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 14,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(970));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 15,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(970));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 16,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(970));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 17,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(970));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 18,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(980));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 19,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(980));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 20,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(980));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 21,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(980));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 22,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(980));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 23,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(990));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 24,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(990));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 25,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(990));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 26,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(990));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 27,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(990));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 28,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(990));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 29,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1000));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 30,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1000));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 31,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1000));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 32,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1000));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 33,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1000));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 34,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1000));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 35,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1010));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 36,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1010));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 37,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1010));

            migrationBuilder.UpdateData(
                table: "ProcedureCodes",
                keyColumn: "ProcedureCodeId",
                keyValue: 38,
                column: "CreatedDate",
                value: new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1010));

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 1,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1130), "dev" });

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 2,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1140), "dev" });

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 3,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1140), "dev" });

            migrationBuilder.UpdateData(
                table: "Providers",
                keyColumn: "ProviderId",
                keyValue: 4,
                columns: new[] { "CreatedDate", "TenantId" },
                values: new object[] { new DateTime(2026, 2, 13, 16, 41, 4, 129, DateTimeKind.Utc).AddTicks(1140), "dev" });

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_Providers_ProviderId",
                table: "Appointments",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Claims_Patients_PatientId",
                table: "Claims",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Claims_Providers_ProviderId",
                table: "Claims",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicalNotes_Patients_PatientId",
                table: "ClinicalNotes",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicalNotes_Providers_ProviderId",
                table: "ClinicalNotes",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PlannedProcedures_ClaimProcedures_ClaimProcedureId",
                table: "PlannedProcedures",
                column: "ClaimProcedureId",
                principalTable: "ClaimProcedures",
                principalColumn: "ClaimProcedureId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Procedures_Appointments_AppointmentId",
                table: "Procedures",
                column: "AppointmentId",
                principalTable: "Appointments",
                principalColumn: "AppointmentId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Procedures_Patients_PatientId",
                table: "Procedures",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "PatientId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Procedures_Providers_ProviderId",
                table: "Procedures",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TreatmentPlans_Providers_ProviderId",
                table: "TreatmentPlans",
                column: "ProviderId",
                principalTable: "Providers",
                principalColumn: "ProviderId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
