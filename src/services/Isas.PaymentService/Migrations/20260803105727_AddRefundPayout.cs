using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundPayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payout_failure_reason",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payout_id",
                table: "orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "payout_idempotency_key",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payout_status",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_payout_in_flight",
                table: "orders",
                column: "updated_at",
                filter: "payout_status = 'InFlight'");

            migrationBuilder.CreateIndex(
                name: "ux_orders_payout_idempotency_key",
                table: "orders",
                column: "payout_idempotency_key",
                unique: true,
                filter: "payout_idempotency_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_payout_status",
                table: "orders",
                sql: "payout_status IS NULL OR payout_status IN ('InFlight', 'Succeeded', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_payout_in_flight",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ux_orders_payout_idempotency_key",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_payout_status",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payout_failure_reason",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payout_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payout_idempotency_key",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payout_status",
                table: "orders");
        }
    }
}
