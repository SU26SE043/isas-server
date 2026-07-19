using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipIdentityF5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "campaign_membership",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "full_name",
                table: "campaign_membership",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // ── Backfill CHỈ đường-2 (shortlist: membership ↔ cv_submission) ────────────────────
            // Deterministic, 1 câu, Postgres `UPDATE ... FROM` (SQLite không chạy — test EnsureCreated
            // bỏ qua toàn bộ migration nên câu này KHÔNG được test nào phủ; đã đọc script sinh ra bằng mắt).
            migrationBuilder.Sql(
                "UPDATE campaign_membership m SET email = s.email, full_name = s.full_name " +
                "FROM cv_submission s WHERE m.cv_submission_id = s.id;");

            // 🔴 CỐ Ý KHÔNG BACKFILL ĐƯỜNG-1 (mời-thẳng email, cv_submission_id IS NULL).
            // KHÔNG có khoá nối `campaign_membership` ↔ `campaign_invitations`: `invitations.session_id`
            // không ai set, và invitation không mang `candidate_id`. Ghép theo (campaign_id, email) là SUY
            // ĐOÁN — nhiều lời mời có thể cùng campaign, và một email có thể được mời lại/reissue.
            // Email SAI trong file HR xuất ra (rồi HR gửi thư cho nhầm người) TỆ HƠN ô trống.
            // Đường-1 được điền FORWARD ở `ParticipationService.JoinCampaignAsync` (lần join kế tiếp),
            // và đường đọc `GetIdentityByCandidateAsync` đã có fallback sang cv_submission.
            // → Ai đọc migration này về sau: đừng "sửa hộ" bằng cách join theo email.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email",
                table: "campaign_membership");

            migrationBuilder.DropColumn(
                name: "full_name",
                table: "campaign_membership");
        }
    }
}
