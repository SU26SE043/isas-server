using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// RNK1 · HĐ-2 / CAMP-21 — luật "câu HR khai mà ứng viên bỏ trống tính 0 điểm" (SERVER SỞ HỮU).
    ///
    /// <para>AddColumn defaultValue: true ⇒ campaign INSERT từ bản này trở đi bị phạt (mặc định).
    /// Rồi UPDATE campaigns SET skip_penalty = false backfill MỌI campaign đang có về false: KHÔNG
    /// đổi thước đo giữa chiến dịch đang chạy (HĐ-2). AddColumn điền default cho row cũ TRƯỚC khi
    /// UPDATE chạy nên câu UPDATE luôn có cột để ghi.</para>
    /// </summary>
    public partial class AddCampaignSkipPenaltyRnk1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "skip_penalty",
                table: "campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Campaign đã có TRƯỚC RNK1 = false (không phạt hồi tố). Campaign mới nhận true từ default.
            migrationBuilder.Sql("UPDATE campaigns SET skip_penalty = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "skip_penalty",
                table: "campaigns");
        }
    }
}
