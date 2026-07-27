using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddGrantIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "grant_idempotency_key",
                table: "credit_transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "grant_remaining_credits_after",
                table: "credit_transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_credit_transactions_grant_idempotency",
                table: "credit_transactions",
                columns: new[] { "owner_type", "owner_id", "grant_idempotency_key" },
                unique: true,
                filter: "grant_idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_credit_transactions_grant_idempotency",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "grant_idempotency_key",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "grant_remaining_credits_after",
                table: "credit_transactions");
        }
    }
}
