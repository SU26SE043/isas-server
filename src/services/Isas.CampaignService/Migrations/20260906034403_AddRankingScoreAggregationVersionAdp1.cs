using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// ADP1 — con dấu CÁCH GỘP ĐIỂM trên dòng xếp hạng (1 = theo answer · 2 = theo CÂU GỐC), đến qua
    /// event SessionScored.
    ///
    /// THUẦN ADDITIVE: một cột int nullable, KHÔNG default, KHÔNG backfill, KHÔNG raw SQL.
    ///
    /// CỐ Ý KHÔNG BACKFILL = 1 (khác bảng practice_sessions ở chỗ mọi dòng ở đây ĐỀU đã chấm, nên
    /// backfill về mặt dữ liệu là đúng): hai bảng cùng mang một con dấu thì phải cùng một quy ước, và
    /// null-lệch-1 giữa hai bảng sẽ khiến người đọc sau này phải đoán xem cái nào có nghĩa gì. Ngoài
    /// ra dòng ghi trong cửa sổ rollout (Campaign đã lên bản mới, Interview còn bản cũ) cũng cho null,
    /// nên "null = không biết" vẫn phải là nghĩa hợp lệ dù có backfill hay không.
    /// <inheritdoc />
    public partial class AddRankingScoreAggregationVersionAdp1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "score_aggregation_version",
                table: "campaign_rankings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "score_aggregation_version",
                table: "campaign_rankings");
        }
    }
}
