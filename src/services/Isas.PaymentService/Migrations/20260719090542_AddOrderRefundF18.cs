using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderRefundF18 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refund_gateway_ref",
                table: "orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refund_reason",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "refunded_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "refunded_by",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reverses_transaction_id",
                table: "credit_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_credit_transactions_reverses",
                table: "credit_transactions",
                column: "reverses_transaction_id",
                unique: true,
                filter: "reverses_transaction_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_credit_transactions_credit_transactions_reverses_transactio",
                table: "credit_transactions",
                column: "reverses_transaction_id",
                principalTable: "credit_transactions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_credit_transactions_credit_transactions_reverses_transactio",
                table: "credit_transactions");

            migrationBuilder.DropIndex(
                name: "ux_credit_transactions_reverses",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "refund_gateway_ref",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refund_reason",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refunded_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refunded_by",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "reverses_transaction_id",
                table: "credit_transactions");
        }
    }
}
