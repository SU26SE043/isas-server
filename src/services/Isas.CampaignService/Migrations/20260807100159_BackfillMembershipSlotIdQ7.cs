using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// Q7 — điền <c>campaign_membership.slot_id</c> cho membership ĐÃ join trước bản vá.
    ///
    /// Cột này có từ <c>AddCampaignSlotsAndCapacity</c> nhưng KHÔNG đường ghi nào chạm tới: khung giờ
    /// chỉ được ghi lên <c>campaign_invitations</c>, còn cả 4 đường đọc lại đọc trên membership (guard
    /// khung giờ lúc Start · <c>StartedCount</c> mỗi slot · guard "không xoá khung giờ đang có ứng viên
    /// thi"). Chỉ sửa code thì mọi membership hiện có vĩnh viễn NULL — join lại để điền là bất khả thi
    /// vì lời mời phải còn hiệu lực (chưa revoke, chưa hết hạn).
    ///
    /// KHÔNG đổi schema: migration thuần dữ liệu, model snapshot giữ nguyên.
    /// </summary>
    public partial class BackfillMembershipSlotIdQ7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Khoá nối `invitation_id` (FX1) là quan hệ THẬT ghi tại thời điểm join, không phải suy đoán
            // ghép-bằng-email ⇒ backfill chắc chắn. Membership không có invitation_id (lịch sử trước FX1)
            // CỐ Ý bỏ qua: không khoá nối nào chắc chắn, đoán bừa sẽ gán ứng viên vào khung giờ của
            // người khác — tệ hơn NULL (NULL = "không ràng buộc khung giờ", đúng hành vi họ đang có).
            //
            // `m.slot_id IS NULL` giữ tính idempotent + không bao giờ ghi đè giá trị do code mới ghi;
            // `i.slot_id IS NOT NULL` tránh rewrite row cho phép gán NULL-về-NULL vô nghĩa.
            //
            // Postgres-only (SQLite/EnsureCreated bỏ qua migration) ⇒ KHÔNG test nào phủ được câu này,
            // đã đọc bằng mắt. Kết thúc bằng `;` — thiếu dấu này từng làm vỡ idempotent script lúc deploy
            // (xem AddAuditColumnsAndTypes).
            migrationBuilder.Sql("""
                UPDATE campaign_membership AS m
                SET slot_id = i.slot_id
                FROM campaign_invitations AS i
                WHERE m.invitation_id = i.id
                  AND m.slot_id IS NULL
                  AND i.slot_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CỐ Ý no-op. Sau khi migration này chạy, code mới cũng ghi `slot_id` ở mỗi lần join, nên
            // KHÔNG cách nào phân biệt row do backfill điền với row do đường ghi mới điền. Nulling tất
            // cả sẽ gỡ ràng buộc khung giờ của những buổi thi đang chạy — mất dữ liệu thật để "hoàn tác"
            // một thay đổi vốn chỉ chép lại thông tin đã có ở campaign_invitations.
            // Rollback schema không cần gì ở đây: cột slot_id thuộc AddCampaignSlotsAndCapacity.
        }
    }
}
