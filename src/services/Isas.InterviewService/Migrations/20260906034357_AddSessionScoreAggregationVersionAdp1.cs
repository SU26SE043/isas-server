using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// ADP1 — con dấu CÁCH GỘP ĐIỂM cho mỗi buổi (1 = theo answer · 2 = theo CÂU GỐC).
    ///
    /// THUẦN ADDITIVE: một cột int nullable, KHÔNG default, KHÔNG backfill, KHÔNG raw SQL ⇒ apply
    /// trước hay sau lúc deploy image đều an toàn (code cũ không đọc cột này; code mới trên schema cũ
    /// mới là thứ nổ 42703, nên vẫn nên apply TRƯỚC).
    ///
    /// CỐ Ý KHÔNG BACKFILL = 1, dù mọi buổi ĐÃ CHẤM ở thời điểm chạy migration đúng là gộp theo answer:
    /// bảng này chứa cả buổi CHƯA chấm (Ready/InProgress/Scoring). Những buổi đó sẽ được chấm bằng
    /// code MỚI sau khi deploy ⇒ gán 1 cho chúng là ghi một điều SAI, và sai theo hướng khó phát hiện
    /// nhất (nhãn "thang cũ" trên điểm thang mới). Backfill có điều kiện (`WHERE status = 'Scored'`)
    /// thì đúng hơn nhưng SQLite/EnsureCreated không chạy migration ⇒ không test nào phủ được nó.
    /// null = "không biết" là câu trả lời trung thực và không cần ai kiểm chứng (BK23).
    /// <inheritdoc />
    public partial class AddSessionScoreAggregationVersionAdp1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "score_aggregation_version",
                table: "practice_sessions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "score_aggregation_version",
                table: "practice_sessions");
        }
    }
}
