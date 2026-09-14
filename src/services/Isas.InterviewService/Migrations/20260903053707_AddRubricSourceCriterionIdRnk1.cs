using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRubricSourceCriterionIdRnk1 : Migration
    {
        // RNK1 · HĐ-5 — rubric_criteria.source_criterion_id: id tiêu chí bên CampaignService mà dòng
        // này được materialize từ (ref lỏng, KHÔNG FK xuyên service — GEN-2). Đi vào
        // CriterionInputSnapshot.CriterionId của event SessionScored ⇒ Campaign khớp điểm sàn read-time
        // theo id (ổn định qua PUT) thay vì theo tên.
        //
        // Thuần additive: 1 cột nullable, KHÔNG defaultValue (CẤM của B4) ⇒ mọi dòng đang có
        // (rubric B2C, buổi B2B tạo trước RNK1) nhận NULL và Campaign lùi về khớp theo TÊN.
        // (Scaffolder sinh kèm 42 lệnh `UpdateData(... value: null)` cho seed HasData — thuần no-op,
        // đã gỡ.)

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_criterion_id",
                table: "rubric_criteria",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_criterion_id",
                table: "rubric_criteria");
        }
    }
}
