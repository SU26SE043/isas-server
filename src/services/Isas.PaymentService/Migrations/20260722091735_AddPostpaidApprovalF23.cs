using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddPostpaidApprovalF23 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "due_at",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "paid_at",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_mode",
                table: "credit_reservations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Prepaid");

            migrationBuilder.AddColumn<DateTime>(
                name: "payment_mode_changed_at",
                table: "credit_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "payment_mode_changed_by",
                table: "credit_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_mode_changed_note",
                table: "credit_accounts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_issued_due_at",
                table: "invoices",
                column: "due_at",
                filter: "status = 'Issued'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_accounts_credit_limit_positive",
                table: "credit_accounts",
                sql: "credit_limit IS NULL OR credit_limit > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_issued_due_at",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_accounts_credit_limit_positive",
                table: "credit_accounts");

            migrationBuilder.DropColumn(
                name: "due_at",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "paid_at",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "payment_mode",
                table: "credit_reservations");

            migrationBuilder.DropColumn(
                name: "payment_mode_changed_at",
                table: "credit_accounts");

            migrationBuilder.DropColumn(
                name: "payment_mode_changed_by",
                table: "credit_accounts");

            migrationBuilder.DropColumn(
                name: "payment_mode_changed_note",
                table: "credit_accounts");
        }
    }
}
