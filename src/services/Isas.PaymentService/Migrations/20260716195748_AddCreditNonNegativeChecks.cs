using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditNonNegativeChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_transactions_delta_nonzero",
                table: "credit_transactions",
                sql: "delta <> 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_accounts_non_negative",
                table: "credit_accounts",
                sql: "remaining_credits >= 0 AND reserved_credits >= 0 AND (period_usage IS NULL OR period_usage >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_transactions_delta_nonzero",
                table: "credit_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_accounts_non_negative",
                table: "credit_accounts");
        }
    }
}
