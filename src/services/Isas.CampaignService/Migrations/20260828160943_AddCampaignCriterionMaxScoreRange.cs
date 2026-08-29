using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCriterionMaxScoreRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dọn dữ liệu TRƯỚC khi đặt CHECK, nếu không migration sẽ FAIL ở môi trường
            // đang có giá trị vượt trần — và fail giữa lúc deploy là kiểu hỏng đắt nhất.
            //
            // Vì sao kẹp là an toàn, không phải mất dữ liệu âm thầm: hàng có max_score > 100
            // ĐANG hỏng sẵn — ScoringCriteriaBuilder dựng dải điểm bằng Enumerable.Range(0, top+1),
            // với 2147483647 thì top+1 TRÀN INT và ném ⇒ answer không bao giờ được chấm.
            // Đo trên production 28/08: đúng 2 hàng vượt trần (1000 và 2147483647), cả hai thuộc
            // campaign `BE-PROBE` trạng thái Draft, 0 ranking và 0 CV ⇒ KHÔNG điểm nào phụ thuộc.
            // Giá trị hợp lệ lớn nhất trong dữ liệu thật là 25, nên trần 100 không chạm ai.
            //
            // ⚠ Mọi câu Sql() PHẢI kết thúc dấu chấm phẩy — thiếu nó làm vỡ idempotent script
            // lúc deploy dù `dotnet ef database update` vẫn chạy (tiền lệ AddAuditColumnsAndTypes).
            migrationBuilder.Sql(
                "UPDATE campaign_criteria SET max_score = 100 WHERE max_score > 100;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_max_score_range",
                table: "campaign_criteria",
                sql: "max_score >= 1 AND max_score <= 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Chỉ gỡ ràng buộc. KHÔNG khôi phục giá trị đã kẹp — giá trị gốc không lưu ở đâu,
            // và chúng vốn là dữ liệu hỏng không chấm được. Down() đưa schema về cũ, không đưa
            // dữ liệu hỏng về cũ.
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_max_score_range",
                table: "campaign_criteria");
        }
    }
}
