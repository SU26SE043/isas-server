using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <summary>
    /// Phạm vi chấm theo câu hỏi:
    ///  · <c>rubric_criteria.scoring_scope</c> — Always (tiêu chí CÁCH NÓI) / WhenTargeted (NỘI DUNG).
    ///  · <c>practice_questions.target_criterion_ids</c> — tiêu chí nội dung câu hỏi nhắm tới (jsonb).
    ///  · <c>practice_sessions.scoring_scope_version</c> — con dấu để BC15/F14/CAMP-10 biết điểm buổi
    ///    này có so sánh được với buổi cũ hay không.
    ///
    /// <para>🔴 <b>ĐÃ SỬA TAY — đọc kỹ trước khi scaffold lại.</b> Bản EF sinh ra chứa <b>24</b> lệnh
    /// <c>UpdateData</c> RỖNG (<c>columns: new string[0]</c>) cho các row seed giữ giá trị mặc định.
    /// Chúng render thành <c>UPDATE rubric_criteria SET &lt;trống&gt; WHERE id = '…'</c> — <b>lỗi cú
    /// pháp Postgres</b>, làm nổ cả transaction migration. SQLite/<c>EnsureCreated</c> KHÔNG chạy
    /// migration nên toàn bộ test vẫn xanh 100% và không có gì báo. Đã xoá 24 lệnh no-op đó; 24 row
    /// ấy nhận 'Always' từ chính DEFAULT của cột. Cùng hạng lỗi với <c>defaultValue: ""</c> cho cột
    /// jsonb (F15) và <c>migrationBuilder.Sql()</c> thiếu dấu <c>;</c> (AddAuditColumnsAndTypes).</para>
    ///
    /// <para>Kiểm số: 42 row seed = 18 row NỘI DUNG (9 tiêu chí × 2 ngôn ngữ) đổi sang 'WhenTargeted'
    /// + 24 row CÁCH NÓI giữ 'Always'.</para>
    ///
    /// <para>Thuần additive (3 cột mới, 2 nullable, 1 có DEFAULT) ⇒ không cần dọn dữ liệu trước;
    /// code cũ đang chạy không đọc cột nào trong đây nên apply trước hay sau deploy đều được.</para>
    /// </summary>
    public partial class AddScoringScopeAndQuestionTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DEFAULT 'Always' backfill mọi row đang có (rubric riêng BC16 + tiêu chí campaign B2B)
            // ⇒ chúng giữ NGUYÊN hành vi "chấm mọi câu" như trước, không cần backfill tay.
            migrationBuilder.AddColumn<string>(
                name: "scoring_scope",
                table: "rubric_criteria",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Always");

            // NULLABLE và CỐ Ý không default: row cũ = "không biết buổi đó chấm theo phạm vi nào" (BK23).
            migrationBuilder.AddColumn<int>(
                name: "scoring_scope_version",
                table: "practice_sessions",
                type: "integer",
                nullable: true);

            // NULLABLE ⇒ không cần defaultValue cho cột jsonb (né bug F15: chuỗi rỗng không phải JSON
            // hợp lệ nên Postgres từ chối ngay tại ALTER TABLE).
            migrationBuilder.AddColumn<string>(
                name: "target_criterion_ids",
                table: "practice_questions",
                type: "jsonb",
                nullable: true);

            // 9 tiêu chí NỘI DUNG × 2 ngôn ngữ. Id 'vi' và id 'en' của cùng một tiêu chí chỉ khác byte
            // đầu (B2CRubricSeed.EnglishId: bytes[0] ^= 0x11) → 0b100000↔0b100011, v.v.
            foreach (var id in new[]
            {
                // BA — Phân tích yêu cầu · Hiểu nghiệp vụ & các bên liên quan · Tư duy giải quyết vấn đề
                "0b100000-0000-0000-0000-000000000001",
                "0b100000-0000-0000-0000-000000000003",
                "0b100000-0000-0000-0000-000000000004",
                "0b100011-0000-0000-0000-000000000001",
                "0b100011-0000-0000-0000-000000000003",
                "0b100011-0000-0000-0000-000000000004",
                // BE — Chiều sâu kỹ thuật · Thiết kế hệ thống & CSDL · Giải quyết vấn đề & thuật toán
                "0be00000-0000-0000-0000-000000000001",
                "0be00000-0000-0000-0000-000000000002",
                "0be00000-0000-0000-0000-000000000003",
                "0be00011-0000-0000-0000-000000000001",
                "0be00011-0000-0000-0000-000000000002",
                "0be00011-0000-0000-0000-000000000003",
                // FE — Chiều sâu kỹ thuật · Ý thức UI/UX & accessibility · Giải quyết vấn đề
                "0fe00000-0000-0000-0000-000000000001",
                "0fe00000-0000-0000-0000-000000000002",
                "0fe00000-0000-0000-0000-000000000003",
                "0fe00011-0000-0000-0000-000000000001",
                "0fe00011-0000-0000-0000-000000000002",
                "0fe00011-0000-0000-0000-000000000003",
            })
            {
                migrationBuilder.UpdateData(
                    table: "rubric_criteria",
                    keyColumn: "id",
                    keyValue: new Guid(id),
                    column: "scoring_scope",
                    value: "WhenTargeted");
            }

            migrationBuilder.AddCheckConstraint(
                name: "ck_rubric_criteria_scoring_scope",
                table: "rubric_criteria",
                sql: "scoring_scope IN ('Always', 'WhenTargeted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Không cần hoàn 18 giá trị 'WhenTargeted': DropColumn ngay dưới xoá luôn cả cột.
            migrationBuilder.DropCheckConstraint(
                name: "ck_rubric_criteria_scoring_scope",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "scoring_scope",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "scoring_scope_version",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "target_criterion_ids",
                table: "practice_questions");
        }
    }
}
