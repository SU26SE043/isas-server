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
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_owner_grant_idempotency",
                table: "subscriptions",
                columns: new[] { "owner_type", "owner_id", "admin_grant_idempotency_key" },
                unique: true,
                filter: "admin_grant_idempotency_key IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_subscriptions_owner_grant_idempotency",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "admin_grant_idempotency_key",
                table: "subscriptions");
        }
    }
}
