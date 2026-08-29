using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class DropCvScreeningPassScorePctSeedB9 : Migration
    {
        // SCP1 · B9 — sàng CV không có khái niệm đạt/trượt (không cột, không consumer, không màn hiển
        // thị). Hai mẫu hệ thống CvScreening từng seed pass_score_pct = 50 = lời hứa với employer về
        // một quyết định không tồn tại. Bước này XOÁ lời hứa thừa, không xây thêm.
        //
        // Thuần UpdateData (không DELETE+INSERT như EF scaffold gợi ý cho HasData của cột
        // immutable-after-save) — hai dòng UPDATE tại chỗ, an toàn trên DB đã có dữ liệu.
        private static readonly Guid TplLikeNow = new("5c900004-0000-0000-0000-000000000000");
        private static readonly Guid TplMustHave = new("5c900005-0000-0000-0000-000000000000");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "scoring_policies",
                keyColumn: "id",
                keyValue: TplLikeNow,
                column: "pass_score_pct",
                value: null);

            migrationBuilder.UpdateData(
                table: "scoring_policies",
                keyColumn: "id",
                keyValue: TplMustHave,
                column: "pass_score_pct",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "scoring_policies",
                keyColumn: "id",
                keyValue: TplLikeNow,
                column: "pass_score_pct",
                value: 50);

            migrationBuilder.UpdateData(
                table: "scoring_policies",
                keyColumn: "id",
                keyValue: TplMustHave,
                column: "pass_score_pct",
                value: 50);
        }
    }
}
