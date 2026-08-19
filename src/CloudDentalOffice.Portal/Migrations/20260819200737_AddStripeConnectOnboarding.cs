using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudDentalOffice.Portal.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeConnectOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ChargesEnabled",
                table: "PaymentProcessorConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DetailsSubmitted",
                table: "PaymentProcessorConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastStatusCode",
                table: "PaymentProcessorConfigurations",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnboardingStatus",
                table: "PaymentProcessorConfigurations",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.AddColumn<bool>(
                name: "PayoutsEnabled",
                table: "PaymentProcessorConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargesEnabled",
                table: "PaymentProcessorConfigurations");

            migrationBuilder.DropColumn(
                name: "DetailsSubmitted",
                table: "PaymentProcessorConfigurations");

            migrationBuilder.DropColumn(
                name: "LastStatusCode",
                table: "PaymentProcessorConfigurations");

            migrationBuilder.DropColumn(
                name: "OnboardingStatus",
                table: "PaymentProcessorConfigurations");

            migrationBuilder.DropColumn(
                name: "PayoutsEnabled",
                table: "PaymentProcessorConfigurations");
        }
    }
}
