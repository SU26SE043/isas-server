using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceOwnerPeriodEndUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_owner_type_owner_id",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_owner_period_end",
                table: "invoices",
                columns: new[] { "owner_type", "owner_id", "period_end" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_invoices_owner_period_end",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_owner_type_owner_id",
                table: "invoices",
                columns: new[] { "owner_type", "owner_id" });
        }
    }
}
