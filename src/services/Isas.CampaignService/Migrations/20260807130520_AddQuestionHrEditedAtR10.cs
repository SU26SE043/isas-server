using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// R10 — <c>campaign_questions.hr_edited_at</c>: mốc HR sửa NỘI DUNG một câu do AI sinh.
    ///
    /// F9 ("sinh lại câu hỏi") xoá MỌI row <c>source = 'AiGenerated'</c>. Câu AI mà HR đã ngồi chỉnh
    /// vẫn giữ nhãn <c>AiGenerated</c> (đúng theo F10 — provenance là sự thật do server sở hữu, không
    /// đổi khi HR sửa) nên nó bị xoá cùng, mất trắng công sức, không cảnh báo, không khôi phục được.
    /// Cột này là thứ duy nhất phân biệt được hai nhóm đó.
    ///
    /// THUẦN ADDITIVE: 1 cột nullable, không default, không backfill, không raw SQL ⇒ apply trước hay
    /// sau khi deploy code mới đều an toàn (code cũ không biết cột này; code mới đọc NULL = "chưa ai sửa"
    /// = đúng hành vi hiện tại). Reversible.
    ///
    /// ⚠ KHÔNG backfill được, và đó là giới hạn có thật: hệ thống chưa từng ghi lịch sử sửa câu hỏi
    /// (<c>campaign_questions</c> không có <c>updated_at</c>, audit chỉ ghi số lượng giữ/thêm/xoá chứ
    /// không ghi câu nào) ⇒ mọi câu AI HR đã chỉnh TRƯỚC mốc này vẫn NULL và vẫn bị lượt sinh kế thay.
    /// Đoán bừa (vd "coi mọi câu AI hiện có là đã chỉnh") sẽ làm câu AI thật không bao giờ làm mới được.
    /// </summary>
    public partial class AddQuestionHrEditedAtR10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "hr_edited_at",
                table: "campaign_questions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hr_edited_at",
                table: "campaign_questions");
        }
    }
}
