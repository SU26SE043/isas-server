using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundSettledAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "refund_settled_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill đơn ĐÃ hoàn từ trước: có mã tham chiếu = coi như đã chuyển tiền xong (settled). Đơn hoàn
            // không mã để NULL = "chờ chuyển tiền" (kế toán rà lại). Chỉ chạy trên Postgres (SQLite/EnsureCreated
            // bỏ qua migration); status lưu string nên literal 'Refunded'. PHẢI kết thúc `;` cho idempotent-script.
            migrationBuilder.Sql(
                "UPDATE orders SET refund_settled_at = refunded_at " +
                "WHERE status = 'Refunded' AND refund_gateway_ref IS NOT NULL AND refund_settled_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refund_settled_at",
                table: "orders");
        }
    }
}
