using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCampaignRubricVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "campaign_rubric_version",
                table: "practice_sessions",
                type: "integer",
                nullable: true);

            // BACKFILL — BẮT BUỘC, và "1" ở đây là điều ĐÃ BIẾT CHẮC chứ không phải phỏng đoán:
            // đường materialize cũ (PracticeService) hardcode `Version = 1` cho MỌI lượt từng chạy,
            // nên mọi buổi B2B đang có trên DB đều đã được chấm bằng bộ v1.
            //
            // Bỏ backfill thì buổi đang chạy dở giữ pin NULL; sau khi HR sửa mốc (bump lên v2, hạ cờ
            // is_active của v1) chúng rơi vào nhánh dự phòng `is_active` của loader ⇒ nạp bộ v2 trong
            // khi điểm đã chấm thuộc v1 ⇒ ĐÚNG cái lỗi "hai thước đo trên một bài" mà thay đổi này
            // sinh ra để chặn.
            //
            // ⚠ SQLite/EnsureCreated BỎ QUA migration ⇒ câu này KHÔNG có test nào phủ — đã đọc bằng
            // mắt. Dấu `;` cuối là bắt buộc để idempotent-script lúc deploy không vỡ (tiền lệ
            // AddAuditColumnsAndTypes thiếu `;` làm hỏng cả script dù `database update` vẫn chạy).
            migrationBuilder.Sql(@"
                UPDATE practice_sessions
                SET campaign_rubric_version = 1
                WHERE campaign_id IS NOT NULL AND campaign_rubric_version IS NULL;
            ");

            // ⚠ APPLY-WINDOW: unique index dưới đây FAIL TO (không cắt cụt dữ liệu) nếu DB thật đã có
            // hai dòng trùng (campaign_id, version, name). Câu kiểm read-only chạy TRƯỚC khi apply:
            //   SELECT campaign_id, version, name, count(*)
            //   FROM rubric_criteria WHERE campaign_id IS NOT NULL
            //   GROUP BY 1,2,3 HAVING count(*) > 1;
            // Kỳ vọng 0 dòng (materialize cũ chạy đúng một lần/campaign, và phía Campaign đã có
            // UNIQUE (campaign_id, name)).
            migrationBuilder.CreateIndex(
                name: "ux_rubric_criteria_campaign_version_name",
                table: "rubric_criteria",
                columns: new[] { "campaign_id", "version", "name" },
                unique: true,
                filter: "campaign_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_rubric_criteria_campaign_version_name",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "campaign_rubric_version",
                table: "practice_sessions");
        }
    }
}
