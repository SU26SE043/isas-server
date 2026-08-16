using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddB2CRubricPinning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "b2c_rubric_owner_id",
                table: "practice_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "b2c_rubric_version",
                table: "practice_sessions",
                type: "integer",
                nullable: true);

            // ── BACKFILL — ba tầng, xếp từ CHẮC CHẮN xuống SUY ĐOÁN ────────────────────────────────
            //
            // ⚠ SQLite/EnsureCreated BỎ QUA migration ⇒ toàn bộ khối này KHÔNG có test nào phủ — đã đọc
            // bằng mắt. Mọi câu kết thúc `;` (tiền lệ AddAuditColumnsAndTypes thiếu `;` làm vỡ
            // idempotent-script lúc deploy dù `database update` vẫn chạy). Cú pháp `UPDATE … FROM` và
            // `DISTINCT ON` là Postgres-only — đúng nơi migration này chạy.
            //
            // TẦNG 1 — SỰ THẬT ghi lại được. `session_criterion_scores.criterion_id` trỏ thẳng vào chính
            // những dòng rubric đã dùng để chấm buổi đó, nên chủ + phiên bản đọc ra từ đây là điều ĐÃ
            // BIẾT CHẮC chứ không phải suy từ trạng thái hôm nay. Tầng này phủ mọi buổi CÓ màn kết quả
            // để hiển thị — tức mọi buổi mà con dấu nói ra điều gì đó với người dùng.
            migrationBuilder.Sql(@"
                UPDATE practice_sessions s
                SET b2c_rubric_owner_id = e.candidate_id,
                    b2c_rubric_version  = e.version
                FROM (
                    SELECT DISTINCT ON (scs.session_id)
                           scs.session_id, rc.candidate_id, rc.version
                    FROM session_criterion_scores scs
                    JOIN rubric_criteria rc ON rc.id = scs.criterion_id
                    WHERE rc.campaign_id IS NULL
                    ORDER BY scs.session_id, rc.version DESC
                ) e
                WHERE s.id = e.session_id
                  AND s.campaign_id IS NULL
                  AND s.b2c_rubric_version IS NULL;
            ");

            // TẦNG 2 — buổi chưa có breakdown (chủ yếu: đang dở) mà chủ buổi CÓ rubric riêng đang hiệu
            // lực ⇒ ghim đúng bộ đó. Với buổi đang dở đây không phải suy đoán: đó CHÍNH LÀ bộ mà đường
            // chấm sẽ chọn nếu không có con dấu, nên ghim vào chỉ là đóng băng hành vi hiện tại.
            //
            // ⚠ KHÔNG được bỏ tầng này để backfill thẳng "1, bộ chuẩn" cho tất cả: buổi đang dở của
            // ứng viên có rubric riêng sẽ bị chuyển sang bộ chuẩn ⇒ publish-time đã gửi id rubric riêng
            // còn callback-time nạp id bộ chuẩn ⇒ guard E8 coi mọi id là lạ và BỎ ⇒ answer mất sạch
            // điểm. Đúng cái lỗi mà con dấu này sinh ra để chặn, chỉ đảo chiều.
            migrationBuilder.Sql(@"
                UPDATE practice_sessions s
                SET b2c_rubric_owner_id = own.candidate_id,
                    b2c_rubric_version  = own.version
                FROM (
                    SELECT DISTINCT ON (candidate_id, job_category, language)
                           candidate_id, job_category, language, version
                    FROM rubric_criteria
                    WHERE campaign_id IS NULL AND candidate_id IS NOT NULL AND is_active
                    ORDER BY candidate_id, job_category, language, version DESC
                ) own
                WHERE s.campaign_id IS NULL
                  AND s.b2c_rubric_version IS NULL
                  AND s.candidate_id = own.candidate_id
                  AND s.job_category = own.job_category
                  AND s.language     = own.language;
            ");

            // TẦNG 3 — phần còn lại dùng bộ chuẩn. "1" ở đây là điều đã biết chắc tại thời điểm apply:
            // bộ chuẩn được giao bằng `HasData` với `Version = 1`, và trước màn admin (đợt này) KHÔNG có
            // đường nào tạo phiên bản mới cho bộ `candidate_id IS NULL` — `RubricLibraryService` chỉ
            // đánh số trong phạm vi của MỘT candidate.
            // ⚠ Phải chạy SAU tầng 2, nếu không nó nuốt hết cả nhóm có rubric riêng.
            migrationBuilder.Sql(@"
                UPDATE practice_sessions
                SET b2c_rubric_version = 1
                WHERE campaign_id IS NULL AND b2c_rubric_version IS NULL;
            ");

            // Khoá chống hai admin cùng bấm Lưu trên một (nghề, ngôn ngữ) — xem ghi chú đầy đủ ở
            // RubricCriterionConfiguration. Đọc `max(version)` KHÔNG phải trọng tài; index này mới là.
            //
            // ⚠ APPLY-WINDOW: câu này FAIL TO (không cắt cụt dữ liệu) nếu DB thật đã có hai dòng trùng.
            // Câu kiểm read-only chạy TRƯỚC khi apply:
            //   SELECT job_category, language, version, name, count(*)
            //   FROM rubric_criteria WHERE campaign_id IS NULL AND candidate_id IS NULL
            //   GROUP BY 1,2,3,4 HAVING count(*) > 1;
            // Kỳ vọng 0 dòng (seed = 7 tiêu chí × 3 nghề × 2 ngôn ngữ, tên phân biệt trong mỗi nhóm).
            migrationBuilder.CreateIndex(
                name: "ux_rubric_criteria_b2c_default_version_name",
                table: "rubric_criteria",
                columns: new[] { "job_category", "language", "version", "name" },
                unique: true,
                filter: "campaign_id IS NULL AND candidate_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_rubric_criteria_b2c_default_version_name",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "b2c_rubric_owner_id",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "b2c_rubric_version",
                table: "practice_sessions");
        }
    }
}
