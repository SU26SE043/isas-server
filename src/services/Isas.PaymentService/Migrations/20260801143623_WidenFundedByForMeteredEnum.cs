using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class WidenFundedByForMeteredEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "funded_by",
                table: "credit_reservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Credit",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "Credit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "funded_by",
                table: "credit_reservations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Credit",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "Credit");
        }
    }
}
