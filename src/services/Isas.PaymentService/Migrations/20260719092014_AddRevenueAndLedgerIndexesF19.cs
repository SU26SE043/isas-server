using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddRevenueAndLedgerIndexesF19 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credit_transactions_owner_type_owner_id",
                table: "credit_transactions");

            migrationBuilder.CreateIndex(
                name: "ix_orders_paid_at",
                table: "orders",
                column: "paid_at",
                filter: "status = 'Paid'");

            migrationBuilder.CreateIndex(
                name: "ix_credit_transactions_owner_created",
                table: "credit_transactions",
                columns: new[] { "owner_type", "owner_id", "created_at", "id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_paid_at",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_credit_transactions_owner_created",
                table: "credit_transactions");

            migrationBuilder.CreateIndex(
                name: "ix_credit_transactions_owner_type_owner_id",
                table: "credit_transactions",
                columns: new[] { "owner_type", "owner_id" });
        }
    }
}
