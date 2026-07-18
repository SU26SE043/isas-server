using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// DB23 — `campaign_invitations.token` (THÔ) → `token_hash` (SHA-256 base64) + `expires_at` NOT NULL.
    ///
    /// **Token cũ VẪN DÙNG ĐƯỢC.** Không cần "hash ngược": DB đang giữ token thô nên ta băm CHÍNH nó
    /// tại chỗ (`sha256(convert_to(token,'UTF8'))` → base64, khớp byte-for-byte với
    /// `InvitationTokens.Hash` bên C#) → link magic-link đã gửi trong email vẫn redeem được, và bản
    /// thô bị **ghi đè mất** khỏi DB (đó là mục đích bảo mật). ⚠ Cần PostgreSQL ≥ 11 (`sha256()` built-in).
    ///
    /// ⚠ **Down KHÔNG khôi phục được token thô** (đã bị băm đè — một chiều). Rollback = schema về cũ
    /// nhưng cột chứa hash → mọi lời mời đang mở CHẾT, phải reissue (D4). Schema thì reversible.
    /// </summary>
    public partial class HashInvitationTokenDb23 : Migration
    {
        // Khớp InvitationSettings.DefaultExpiryDays — backfill row cũ có expires_at NULL.
        private const int DefaultExpiryDays = 14;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Băm tại chỗ token thô đang lưu → invitation cũ vẫn redeem được, DB hết giữ bản thô.
            migrationBuilder.Sql(
                "UPDATE campaign_invitations " +
                "SET token = encode(sha256(convert_to(token, 'UTF8')), 'base64');");

            // 2) expires_at NULL (campaign không deadline) = token sống VĨNH VIỄN → cấp hạn mặc định
            //    tính từ lúc tạo. Row quá cũ sẽ thành đã-hết-hạn (đúng ý: link vô thời hạn phải chết).
            migrationBuilder.Sql(
                $"UPDATE campaign_invitations " +
                $"SET expires_at = created_at + interval '{DefaultExpiryDays} days' " +
                $"WHERE expires_at IS NULL;");

            migrationBuilder.RenameColumn(
                name: "token",
                table: "campaign_invitations",
                newName: "token_hash");

            migrationBuilder.RenameIndex(
                name: "ix_campaign_invitations_token",
                table: "campaign_invitations",
                newName: "ix_campaign_invitations_token_hash");

            // Không đặt defaultValue: backfill (2) đã dọn sạch NULL nên SET NOT NULL chạy được;
            // để default `0001-01-01` sẽ giấu lỗi dữ liệu bẩn thành "token hết hạn từ năm 1".
            migrationBuilder.AlterColumn<DateTime>(
                name: "expires_at",
                table: "campaign_invitations",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema về nguyên trạng. KHÔNG khôi phục token thô (một chiều — xem chú thích lớp).
            migrationBuilder.AlterColumn<DateTime>(
                name: "expires_at",
                table: "campaign_invitations",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.RenameIndex(
                name: "ix_campaign_invitations_token_hash",
                table: "campaign_invitations",
                newName: "ix_campaign_invitations_token");

            migrationBuilder.RenameColumn(
                name: "token_hash",
                table: "campaign_invitations",
                newName: "token");
        }
    }
}
