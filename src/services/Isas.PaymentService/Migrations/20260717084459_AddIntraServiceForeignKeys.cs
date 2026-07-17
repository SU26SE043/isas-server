using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddIntraServiceForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credit_accounts_owner_type_owner_id",
                table: "credit_accounts");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_credit_accounts_owner_type_owner_id",
                table: "credit_accounts",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_transactions_owner_type_owner_id",
                table: "credit_transactions",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_reservations_owner_type_owner_id",
                table: "credit_reservations",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_credit_reservations_credit_accounts_owner_type_owner_id",
                table: "credit_reservations",
                columns: new[] { "owner_type", "owner_id" },
                principalTable: "credit_accounts",
                principalColumns: new[] { "owner_type", "owner_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_credit_transactions_credit_accounts_owner_type_owner_id",
                table: "credit_transactions",
                columns: new[] { "owner_type", "owner_id" },
                principalTable: "credit_accounts",
                principalColumns: new[] { "owner_type", "owner_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_credit_accounts_owner_type_owner_id",
                table: "invoices",
                columns: new[] { "owner_type", "owner_id" },
                principalTable: "credit_accounts",
                principalColumns: new[] { "owner_type", "owner_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_credit_reservations_credit_accounts_owner_type_owner_id",
                table: "credit_reservations");

            migrationBuilder.DropForeignKey(
                name: "fk_credit_transactions_credit_accounts_owner_type_owner_id",
                table: "credit_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_invoices_credit_accounts_owner_type_owner_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_credit_transactions_owner_type_owner_id",
                table: "credit_transactions");

            migrationBuilder.DropIndex(
                name: "ix_credit_reservations_owner_type_owner_id",
                table: "credit_reservations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_credit_accounts_owner_type_owner_id",
                table: "credit_accounts");

            migrationBuilder.CreateIndex(
                name: "ix_credit_accounts_owner_type_owner_id",
                table: "credit_accounts",
                columns: new[] { "owner_type", "owner_id" },
                unique: true);
        }
    }
}
