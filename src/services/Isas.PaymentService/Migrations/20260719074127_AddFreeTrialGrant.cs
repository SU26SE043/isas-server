using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <summary>
    /// F7 — suất dùng thử B2C. Thuần schema: thêm <c>free_credits_granted</c> (NOT NULL DEFAULT 0, không
    /// rewrite bảng) và mở rộng CHECK số-dư-không-âm sang cột mới.
    ///
    /// CỐ Ý KHÔNG backfill dữ liệu. Ví chỉ được tạo lazy ở webhook Paid, nên <c>credit_accounts</c> hiện
    /// chỉ chứa những chủ ví ĐÃ TRẢ TIỀN; một câu <c>UPDATE ... WHERE owner_type='User'</c> sẽ tặng đúng
    /// nhóm khách đã trả tiền và KHÔNG chạm được user nào đang kẹt 402 — vì nhóm đó chưa có row nào ở đây.
    /// Họ được phục vụ bởi đường tạo-ví-lúc-reserve (<c>CreditAccountService.EnsureTrialWalletAsync</c>).
    ///
    /// ⚠ Apply-window: drop + add lại CHECK khiến Postgres quét kiểm tra toàn bảng dưới ACCESS EXCLUSIVE.
    /// Bảng tiền hiện rất nhỏ nên không đáng kể; nếu về sau bảng lớn thì tách thành NOT VALID + VALIDATE.
    /// </summary>
    public partial class AddFreeTrialGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_accounts_non_negative",
                table: "credit_accounts");

            migrationBuilder.AddColumn<int>(
                name: "free_credits_granted",
                table: "credit_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_accounts_non_negative",
                table: "credit_accounts",
                sql: "remaining_credits >= 0 AND reserved_credits >= 0 AND free_credits_granted >= 0 AND (period_usage IS NULL OR period_usage >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_accounts_non_negative",
                table: "credit_accounts");

            migrationBuilder.DropColumn(
                name: "free_credits_granted",
                table: "credit_accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_accounts_non_negative",
                table: "credit_accounts",
                sql: "remaining_credits >= 0 AND reserved_credits >= 0 AND (period_usage IS NULL OR period_usage >= 0)");
        }
    }
}
