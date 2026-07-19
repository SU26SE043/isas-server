using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class LinkMembershipToInvitationFx1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Thu độ dài về đúng nguồn dữ liệu ────────────────────────────────────────────────
            // email 320→255, full_name 256→255. F5 chú thích "khớp cv_submission" nhưng KHÔNG khớp:
            // cv_submission.email / campaign_invitations.email / cv_submission.full_name đều varchar(255).
            // Hai cột này CHỈ được sao từ đúng 2 nguồn đó (ParticipationService.ApplyInvitationLink) nên
            // không thể dài hơn 255 ⇒ thu về an toàn, và biến chú thích sai thành ràng buộc thật.
            // ⚠ Apply-window: nếu DB thật vẫn có giá trị > 255 thì lệnh này FAIL TO — fail rõ ràng, KHÔNG
            // cắt cụt dữ liệu. Kiểm trước (read-only):
            //   SELECT count(*) FROM campaign_membership WHERE length(email) > 255 OR length(full_name) > 255;
            migrationBuilder.AlterColumn<string>(
                name: "full_name",
                table: "campaign_membership",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "campaign_membership",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            // ── FX1: quan hệ membership → invitation mà DB16 bỏ quên khi tách bảng God ─────────
            migrationBuilder.AddColumn<Guid>(
                name: "invitation_id",
                table: "campaign_membership",
                type: "uuid",
                nullable: true);

            // ── Backfill CHỈ khi CHẮC CHẮN: đường-2 (shortlist) và duy nhất 1 lời mời khớp ─────
            // Đường-2 có khoá nối thật: membership.cv_submission_id được gán TỪ invitation.campaign_candidate_id
            // lúc join (ParticipationService), nên nếu trong campaign đó CHỈ CÓ MỘT lời mời trỏ về cùng
            // cv_submission thì đó bắt buộc là lời mời đã dẫn tới membership này — suy ra được, không đoán.
            //
            // Điều kiện count(*) = 1 KHÔNG phải cho vui: reissue (D4) tạo THÊM lời mời cùng
            // campaign_candidate_id. Thiếu nó thì `UPDATE ... FROM` có nhiều dòng khớp và Postgres chọn
            // MỘT dòng KHÔNG XÁC ĐỊNH ⇒ FK trỏ vào lời mời tuỳ hứng. Với các ca đó để NULL và đường đọc
            // (GetInvitationsAsync) tự rơi về fallback cũ.
            //
            // 🔴 CỐ Ý KHÔNG BACKFILL ĐƯỜNG-1 (mời-thẳng email, cv_submission_id IS NULL) — cùng lý do đã
            // ghi trong AddMembershipIdentityF5: không có khoá nối nào; ghép theo (campaign_id, email) là
            // SUY ĐOÁN, và một email có thể được mời nhiều lần / reissue. FK trỏ SAI còn tệ hơn FK NULL
            // (NULL thì đường đọc biết là "chưa biết" và fallback; trỏ sai thì mọi tầng trên tin là đúng).
            // Đường-1 được điền FORWARD ở lần join kế tiếp (ApplyInvitationLink chạy ở CẢ 2 nhánh).
            // → Ai đọc migration này về sau: đừng "sửa hộ" bằng cách join theo email.
            //
            // (SQLite/EnsureCreated bỏ qua toàn bộ migration ⇒ câu này KHÔNG test nào phủ — đã đọc bằng mắt.)
            migrationBuilder.Sql(
                "UPDATE campaign_membership m SET invitation_id = i.id " +
                "FROM campaign_invitations i " +
                "WHERE m.invitation_id IS NULL " +
                "  AND m.cv_submission_id IS NOT NULL " +
                "  AND i.campaign_id = m.campaign_id " +
                "  AND i.campaign_candidate_id = m.cv_submission_id " +
                "  AND (SELECT count(*) FROM campaign_invitations x " +
                "       WHERE x.campaign_id = m.campaign_id " +
                "         AND x.campaign_candidate_id = m.cv_submission_id) = 1;");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_invitation_id",
                table: "campaign_membership",
                column: "invitation_id",
                filter: "invitation_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_campaign_membership_invitation_invitation_id",
                table: "campaign_membership",
                column: "invitation_id",
                principalTable: "campaign_invitations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_campaign_membership_invitation_invitation_id",
                table: "campaign_membership");

            migrationBuilder.DropIndex(
                name: "ix_campaign_membership_invitation_id",
                table: "campaign_membership");

            migrationBuilder.DropColumn(
                name: "invitation_id",
                table: "campaign_membership");

            migrationBuilder.AlterColumn<string>(
                name: "full_name",
                table: "campaign_membership",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "campaign_membership",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }
    }
}
