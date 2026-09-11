using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <summary>
    /// CAMP-21 (2026-09-11) — bịt kẽ hở "nộp im lặng để né hình phạt bỏ câu": thêm
    /// <c>practice_answers.reject_reason</c> (text, nullable) để lúc chấm phân biệt được bài
    /// <c>Skipped</c> do VAD xác nhận im lặng (<c>'no_speech'</c>, LOẠI khỏi "đã trả lời") với bài
    /// <c>Skipped</c> do bộ chấm của ta hỏng (null, VẪN tính là đã trả lời).
    ///
    /// Thuần ADD COLUMN nullable: dòng cũ nhận NULL = "không biết" ⇒ vị ngữ đọc
    /// (<c>reject_reason IS NULL OR &lt;&gt; 'no_speech'</c>) vẫn tính chúng là đã trả lời ⇒
    /// KHÔNG đổi điểm hồi tố. Không backfill, không Sql(), không CHECK, không HasMaxLength (lý do
    /// trên entity). Apply TRƯỚC hoặc CÙNG LÚC deploy là an toàn — code cũ không đọc cột này.
    /// </summary>
    public partial class AddAnswerRejectReasonCamp21 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reject_reason",
                table: "practice_answers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reject_reason",
                table: "practice_answers");
        }
    }
}
