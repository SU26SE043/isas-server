using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PaymentService.Models;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    [DbContext(typeof(PaymentDbContext))]
    [Migration("20260801064739_AddAdminGrantIdempotencyKey")]
    public partial class AddAdminGrantIdempotencyKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "admin_grant_idempotency_key",
                table: "subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_admin_grant_idempotency_key",
                table: "subscriptions",
                column: "admin_grant_idempotency_key",
                unique: true,
                filter: "admin_grant_idempotency_key IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_admin_grant_idempotency_key",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "admin_grant_idempotency_key",
                table: "subscriptions");
        }
    }
}
