using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <summary>
    /// RNK1 · HĐ-2 / CAMP-21 — ghim luật "câu HR khai mà ứng viên bỏ trống tính 0 điểm" trên từng
    /// buổi. Thuần ADD COLUMN, default false: B2C + mọi buổi B2B đã có tự nhận "không phạt" (đúng —
    /// campaign trước RNK1 backfill skip_penalty = false phía Campaign). Không backfill riêng, không Sql().
    /// </summary>
    public partial class AddSessionSkipPenaltyRnk1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "skip_penalty",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "skip_penalty",
                table: "practice_sessions");
        }
    }
}
