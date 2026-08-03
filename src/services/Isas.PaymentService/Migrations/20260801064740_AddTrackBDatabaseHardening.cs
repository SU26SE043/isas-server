using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackBDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_enums",
                table: "subscriptions",
                sql: "billing_cycle IN ('Monthly', 'Annual') AND status IN ('Active', 'Expired', 'Cancelled') AND audience IN ('B2C', 'B2B') AND interview_funding IN ('Credit', 'Metered', 'Unlimited') AND source IN ('Purchase', 'AdminGrant')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sub_metered_quota",
                table: "subscriptions",
                sql: "interview_funding <> 'Metered' OR monthly_quota IS NOT NULL AND monthly_quota > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_packages_audience",
                table: "product_packages",
                sql: "audience IS NULL OR audience IN ('B2C', 'B2B')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_packages_price_non_negative",
                table: "product_packages",
                sql: "price_vnd >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_packages_type",
                table: "product_packages",
                sql: "type IN ('OneTime', 'Subscription')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_amount_non_negative",
                table: "orders",
                sql: "amount_vnd >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_kind",
                table: "orders",
                sql: "kind IN ('CreditPack', 'InvoiceSettlement', 'SubscriptionPurchase', 'SubscriptionRenewal')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_owner_type",
                table: "orders",
                sql: "owner_type IN ('Org', 'User')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                table: "orders",
                sql: "status IN ('Pending', 'Paid', 'Failed', 'Expired', 'Cancelled', 'Refunded')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_enums",
                table: "invoices",
                sql: "owner_type = 'Org' AND status IN ('Issued', 'Paid', 'Overdue', 'Void')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_transactions_enums",
                table: "credit_transactions",
                sql: "owner_type IN ('Org', 'User') AND reason IN ('Purchase', 'Consume', 'Refund', 'FreeGrant', 'PromoGrant')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_reservations_enums",
                table: "credit_reservations",
                sql: "owner_type IN ('Org', 'User') AND status IN ('Reserved', 'Consumed', 'Released') AND funded_by IN ('Credit', 'Subscription', 'SubscriptionMetered') AND payment_mode IN ('Prepaid', 'Postpaid')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_accounts_enums",
                table: "credit_accounts",
                sql: "owner_type IN ('Org', 'User') AND payment_mode IN ('Prepaid', 'Postpaid') AND status IN ('Active', 'Suspended')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_enums",
                table: "subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sub_metered_quota",
                table: "subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_packages_audience",
                table: "product_packages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_packages_price_non_negative",
                table: "product_packages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_packages_type",
                table: "product_packages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_amount_non_negative",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_kind",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_owner_type",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_enums",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_transactions_enums",
                table: "credit_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_reservations_enums",
                table: "credit_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_accounts_enums",
                table: "credit_accounts");

        }
    }
}
