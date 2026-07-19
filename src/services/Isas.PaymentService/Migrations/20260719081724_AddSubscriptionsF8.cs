using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <summary>
    /// F8 — dựng lại bảng <c>subscriptions</c> (DB15 đã drop bản scaffold chết) + thêm cột
    /// <c>credit_reservations.funded_by</c>.
    ///
    /// ⚠ APPLY-WINDOW:
    /// <list type="bullet">
    ///   <item><c>funded_by</c> có DEFAULT <c>'Credit'</c> ⇒ mọi chỗ giữ ĐANG SỐNG được backfill đúng
    ///   nghĩa cũ ("đã trừ ví") ⇒ Consume/Release của chúng vẫn chạy nhánh bút toán như trước, kể cả
    ///   trong lúc image cũ và image mới chạy song song khi rollout.</item>
    ///   <item>FK composite (owner_type, owner_id) → <c>credit_accounts</c> đòi ví tồn tại trước khi ghi
    ///   thuê bao; bảng mới nên KHÔNG có dữ liệu cũ phải dọn/backfill.</item>
    ///   <item>Thuần additive, reversible, KHÔNG có <c>migrationBuilder.Sql()</c> nào (nên không dính bẫy
    ///   thiếu dấu <c>;</c> đã làm vỡ idempotent script ở <c>AddAuditColumnsAndTypes</c>).</item>
    ///   <item>4 index tạo kèm <c>CREATE TABLE</c> trên bảng rỗng → không khoá ghi bảng đang dùng.</item>
    /// </list>
    /// </summary>
    public partial class AddSubscriptionsF8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "funded_by",
                table: "credit_reservations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Credit");

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    owner_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_cycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Active"),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.CheckConstraint("ck_subscriptions_period_positive", "expires_at > started_at");
                    table.ForeignKey(
                        name: "fk_subscriptions_credit_accounts_owner_type_owner_id",
                        columns: x => new { x.owner_type, x.owner_id },
                        principalTable: "credit_accounts",
                        principalColumns: new[] { "owner_type", "owner_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_product_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "product_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_active_expires_at",
                table: "subscriptions",
                column: "expires_at",
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_order_id",
                table: "subscriptions",
                column: "order_id",
                unique: true,
                filter: "order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_owner_active",
                table: "subscriptions",
                columns: new[] { "owner_type", "owner_id", "expires_at" },
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_package_id",
                table: "subscriptions",
                column: "package_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropColumn(
                name: "funded_by",
                table: "credit_reservations");
        }
    }
}
