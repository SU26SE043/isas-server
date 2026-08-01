using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionTierSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "activated_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "audience",
                table: "subscriptions",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "B2C");

            migrationBuilder.AddColumn<string>(
                name: "entitlement_hash",
                table: "subscriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "entitlement_snapshot",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "entitlements_version",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "interview_funding",
                table: "subscriptions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Credit");

            migrationBuilder.AddColumn<short>(
                name: "meter_anchor_day",
                table: "subscriptions",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "monthly_quota",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "plan_id",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "subscriptions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Purchase");

            migrationBuilder.AddColumn<string>(
                name: "tier_code",
                table: "subscriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "tier_rank",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "metered_period_start",
                table: "credit_reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "metered_subscription_id",
                table: "credit_reservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "subscription_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_events_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_meters",
                columns: table => new
                {
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_count = table.Column<int>(type: "integer", nullable: false),
                    reserved_count = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_meters", x => new { x.subscription_id, x.period_start });
                    table.CheckConstraint("ck_meter_nonneg", "used_count >= 0 AND reserved_count >= 0");
                    table.ForeignKey(
                        name: "fk_subscription_meters_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_plan_id",
                table: "subscriptions",
                column: "plan_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sub_audience_owner",
                table: "subscriptions",
                sql: "(audience = 'B2C' AND owner_type = 'User') OR (audience = 'B2B' AND owner_type = 'Org')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sub_meter_anchor",
                table: "subscriptions",
                sql: "meter_anchor_day IS NULL OR meter_anchor_day BETWEEN 1 AND 28");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservation_metered_consistency",
                table: "credit_reservations",
                sql: "(funded_by = 'SubscriptionMetered' AND metered_subscription_id IS NOT NULL AND metered_period_start IS NOT NULL) OR (funded_by <> 'SubscriptionMetered' AND metered_subscription_id IS NULL AND metered_period_start IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_events_subscription_id",
                table: "subscription_events",
                column: "subscription_id");

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_plans_plan_id",
                table: "subscriptions",
                column: "plan_id",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_plans_plan_id",
                table: "subscriptions");

            migrationBuilder.DropTable(
                name: "subscription_events");

            migrationBuilder.DropTable(
                name: "subscription_meters");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_plan_id",
                table: "subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sub_audience_owner",
                table: "subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sub_meter_anchor",
                table: "subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservation_metered_consistency",
                table: "credit_reservations");

            migrationBuilder.DropColumn(
                name: "activated_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "entitlement_hash",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "entitlement_snapshot",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "entitlements_version",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "interview_funding",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "meter_anchor_day",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "monthly_quota",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "source",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "tier_code",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "tier_rank",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "metered_period_start",
                table: "credit_reservations");

            migrationBuilder.DropColumn(
                name: "metered_subscription_id",
                table: "credit_reservations");
        }
    }
}
