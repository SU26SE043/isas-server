using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddDb5SweeperIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_credit_reservations_reserved",
                table: "credit_reservations",
                columns: new[] { "owner_type", "owner_id", "created_at" },
                filter: "status = 'Reserved'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credit_reservations_reserved",
                table: "credit_reservations");
        }
    }
}
