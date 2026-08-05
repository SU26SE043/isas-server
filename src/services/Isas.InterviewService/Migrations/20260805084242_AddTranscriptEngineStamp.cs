using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <summary>
    /// Con dấu engine đã chép ra <c>practice_answers.transcript</c> (vd <c>whisper-1</c>,
    /// <c>gemini-2.5-flash</c>, <c>whisper-local-small</c>). AIService chép qua nhà cung cấp TỪ XA và
    /// rơi về Whisper CỤC BỘ khi mạng hỏng ⇒ hai answer cùng buổi có thể dùng hai engine khác nhau,
    /// mà chất lượng chữ (4,2% vs 0,5–0,7% sai số từ) đi thẳng vào điểm chấm — điểm đó vẫn bị đem so
    /// với nhau ở xếp hạng B2B (CAMP-10) và đo cải thiện roadmap (BC15).
    ///
    /// <para>Apply-window: thuần <c>ADD COLUMN</c> nullable, không backfill, không raw SQL, không CHECK
    /// ⇒ online-safe, KHÔNG cần dọn dữ liệu trước. Dòng cũ để NULL = "chép trước bản vá này".</para>
    ///
    /// <para>Cột <c>text</c> chứ không <c>varchar(n)</c> là CÓ CHỦ ĐÍCH: tên model do bên thứ ba đặt và
    /// dài tuỳ ý (<c>gemini-2.5-flash-preview-native-audio-dialog</c> đã 43 ký tự). Chọn độ dài cố định
    /// ở đây là dựng lại bẫy <c>credit_reservations.funded_by varchar(16)</c> — SQLite không enforce độ
    /// dài nên CI xanh 100% trong khi Postgres ném lúc chạy thật.</para>
    /// </summary>
    public partial class AddTranscriptEngineStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "transcript_engine",
                table: "practice_answers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "transcript_engine",
                table: "practice_answers");
        }
    }
}
