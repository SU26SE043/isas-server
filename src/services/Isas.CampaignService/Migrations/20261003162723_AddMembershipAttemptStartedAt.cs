using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipAttemptStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "attempt_started_at",
                table: "campaign_membership",
                type: "timestamp with time zone",
                nullable: true);

            // Điền dữ liệu cũ: membership mới dùng tối đa MỘT lượt thì "lần đầu" chính là "lượt hiện tại"
            // ⇒ chép interview_started_at sang. attempt_count >= 2 để NULL = "không biết" (BK23) — mốc
            // lượt hiện tại chỉ Interview biết (practice_sessions.created_at), không suy được ở DB này.
            // ⚠ attempt_count <= 1 KHÔNG chứng minh tuyệt đối "một lượt" với dòng có trước ATT1 (02/10):
            // khi đó bỏ ngang làm lại được và không được đếm. Đo 03/10 (đối chiếu từng membership với
            // practice_sessions bên Interview): prod 11/11 dòng khớp (mỗi dòng đúng một buổi, lệch < 0,5s);
            // dev 24/25 — 1 dòng 3 buổi làm lại ngày 13/09 ⇒ mốc lệch 18 phút (chỉ dev).
            // Postgres raw SQL — SQLite/EnsureCreated KHÔNG chạy migration nên test .NET không phủ câu này.
            migrationBuilder.Sql(
                "UPDATE campaign_membership SET attempt_started_at = interview_started_at "
                + "WHERE attempt_count <= 1 AND interview_started_at IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempt_started_at",
                table: "campaign_membership");
        }
    }
}
