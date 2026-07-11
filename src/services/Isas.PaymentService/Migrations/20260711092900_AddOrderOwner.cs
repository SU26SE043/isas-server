using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payment_transactions_orders_order_id",
                table: "payment_transactions");

            migrationBuilder.DropIndex(
                name: "ix_payment_transactions_order_id",
                table: "payment_transactions");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "orders",
                newName: "owner_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                table: "payment_transactions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "CreditPack");

            migrationBuilder.AddColumn<string>(
                name: "owner_type",
                table: "orders",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_order_id_created_at",
                table: "payment_transactions",
                columns: new[] { "order_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_payment_transactions_orders_order_id",
                table: "payment_transactions",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payment_transactions_orders_order_id",
                table: "payment_transactions");

            migrationBuilder.DropIndex(
                name: "ix_payment_transactions_order_id_created_at",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "owner_type",
                table: "orders");

            migrationBuilder.RenameColumn(
                name: "owner_id",
                table: "orders",
                newName: "user_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                table: "payment_transactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_order_id",
                table: "payment_transactions",
                column: "order_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_transactions_orders_order_id",
                table: "payment_transactions",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
