using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddPackagePlanLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audience",
                table: "product_packages",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "plan_id",
                table: "product_packages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_packages_plan_id",
                table: "product_packages",
                column: "plan_id");

            migrationBuilder.AddForeignKey(
                name: "fk_product_packages_plans_plan_id",
                table: "product_packages",
                column: "plan_id",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_product_packages_plans_plan_id",
                table: "product_packages");

            migrationBuilder.DropIndex(
                name: "ix_product_packages_plan_id",
                table: "product_packages");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "product_packages");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "product_packages");
        }
    }
}
