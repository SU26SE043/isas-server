using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// CAMP-20 — nới CHECK <c>ck_campaign_criteria_source</c> để nhận giá trị THỨ BA
    /// <c>'SystemDefault'</c> (bộ chuẩn B2C chép về + bộ dự phòng khi AIService lỗi lúc publish).
    ///
    /// <para><b>KHÔNG backfill.</b> <c>'AiSuggested'</c>/<c>'HrEdited'</c> vẫn nằm trong CHECK mới nên
    /// mọi hàng đang có tiếp tục hợp lệ. Hàng <c>AiSuggested</c> cũ do <c>BuildDefaultCriteria</c> sinh
    /// ra thì nhãn vẫn sai, nhưng viết lại hồi tố sẽ đổi luôn cả những hàng AI thật sự sinh — không
    /// phân biệt được hai nhóm đó nữa (cùng nhãn, cùng bảng, không cột nào ghi lại đường nào đã ghi).</para>
    ///
    /// <para>🔴 <b>THỨ TỰ DEPLOY: apply migration NÀY TRƯỚC, rồi mới deploy code ghi giá trị mới.</b>
    /// Ngược lại thì mọi lượt <c>publish</c> rơi vào nhánh AI-lỗi và mọi lượt "dùng bộ chuẩn" đều ném
    /// <c>23514 check constraint violation</c> — tức bản vá cho một đường đã hỏng lại làm nó hỏng nặng
    /// hơn. ⚠ <b>Không test nào bắt được thứ tự sai</b>: SQLite của bộ test dựng schema bằng
    /// <c>EnsureCreated</c> theo model HIỆN TẠI, và model hiện tại đã có <c>'SystemDefault'</c> trong
    /// CHECK — nên test xanh 100% ở cả hai thứ tự deploy. Đây là migration thuần additive, apply sớm
    /// hoàn toàn an toàn (CHECK rộng hơn không từ chối hàng nào).</para>
    ///
    /// <para>⚠ <c>Down()</c> siết CHECK lại: sẽ <b>FAIL</b> nếu đã có hàng <c>source='SystemDefault'</c>.
    /// Đó là hành vi ĐÚNG (fail-loud) — hạ CHECK xuống mà vẫn còn dữ liệu vi phạm nghĩa là dựng lại một
    /// ràng buộc mà chính DB đang phản chứng. Muốn rollback thật thì phải quyết những hàng đó thành
    /// nhãn nào trước đã.</para>
    /// </summary>
    public partial class AddCriterionSourceSystemDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_source",
                table: "campaign_criteria");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_source",
                table: "campaign_criteria",
                sql: "source IN ('AiSuggested', 'HrEdited', 'SystemDefault')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_source",
                table: "campaign_criteria");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_source",
                table: "campaign_criteria",
                sql: "source IN ('AiSuggested', 'HrEdited')");
        }
    }
}
