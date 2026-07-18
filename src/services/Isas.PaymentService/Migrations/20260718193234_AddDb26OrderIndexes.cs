using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddDb26OrderIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_orders_created_id_desc",
                table: "orders",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_orders_owner_created",
                table: "orders",
                columns: new[] { "owner_type", "owner_id", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_expired_at",
                table: "orders",
                column: "expired_at",
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_created_id_desc",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_owner_created",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_pending_expired_at",
                table: "orders");
        }
    }
}
