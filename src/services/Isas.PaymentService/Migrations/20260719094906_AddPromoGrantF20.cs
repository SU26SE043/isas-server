using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddPromoGrantF20 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "granted_by",
                table: "credit_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "credit_transactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "granted_by",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "note",
                table: "credit_transactions");
        }
    }
}
