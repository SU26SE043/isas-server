using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// BK25/DATA-3 — bảng <c>face_images</c>: sổ theo dõi ảnh sinh trắc học đã đẩy lên SeaweedFS,
    /// để có thứ mà LIỆT KÊ và PURGE (trước đây key ảnh live bị vứt đi ⇒ object mồ côi).
    ///
    /// Thuần ADDITIVE (1 bảng + 3 index + 1 CHECK) — không đụng bảng nào đang chạy, apply lúc nào
    /// cũng được, không cần dọn dữ liệu trước.
    ///
    /// ⚠ BACKFILL ảnh THAM CHIẾU (Postgres-only, SQLite/EnsureCreated bỏ qua migration ⇒ KHÔNG test
    /// nào phủ, phải đọc bằng mắt — tiền lệ `AddAuditColumnsAndTypes` thiếu `;` và
    /// `AddLessonResourcesF15` với `defaultValue: ""` trên jsonb).
    /// Chỉ backfill được ảnh THAM CHIẾU vì key của chúng nằm sẵn trong
    /// <c>campaign_membership.reference_image_key</c>. Ảnh LIVE cũ KHÔNG backfill được — không cột
    /// nào từng lưu key của chúng; xem báo cáo BK25 về ảnh mồ côi tồn đọng.
    /// <c>captured_at</c> lấy <c>updated_at</c> của membership: thời điểm chụp thật không có ở đâu,
    /// và <c>updated_at</c> là mốc MUỘN NHẤT biết được ⇒ chọn nó để purge muộn nhất có thể
    /// (bảo thủ đúng chiều cho một job xoá dữ liệu).
    /// </summary>
    public partial class AddFaceImageRetentionBk25 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "face_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_face_images", x => x.id);
                    table.CheckConstraint("ck_face_images_kind", "kind IN ('Live', 'Reference')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_face_images_campaign_id_session_id",
                table: "face_images",
                columns: new[] { "campaign_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_face_images_captured_at",
                table: "face_images",
                column: "captured_at");

            migrationBuilder.CreateIndex(
                name: "ix_face_images_storage_key",
                table: "face_images",
                column: "storage_key",
                unique: true);

            // ── BACKFILL ảnh THAM CHIẾU đang có (chỉ Postgres) ───────────────────────────────
            // Đặt SAU khi unique index đã tồn tại vì `ON CONFLICT (storage_key)` cần nó.
            // `ON CONFLICT DO NOTHING` là lưới an toàn: key chứa cả campaign_id lẫn candidate_id mà
            // campaign_membership đã UNIQUE(campaign_id, candidate_id) nên về lý không thể trùng —
            // nhưng migration không phải chỗ để đánh cược vào một suy luận.
            // `candidate_id IS NOT NULL`: cột đó nullable ở membership còn face_images.candidate_id
            // thì NOT NULL. Dòng bị bỏ (nếu có) chỉ mất khả năng auto-purge, không mất dữ liệu.
            // KHÔNG có Down() riêng — Down của migration này DROP luôn cả bảng.
            migrationBuilder.Sql(@"
                INSERT INTO face_images (id, campaign_id, candidate_id, session_id, kind, storage_key, captured_at)
                SELECT gen_random_uuid(), m.campaign_id, m.candidate_id, NULL, 'Reference',
                       m.reference_image_key, m.updated_at
                FROM campaign_membership m
                WHERE m.reference_image_key IS NOT NULL
                  AND btrim(m.reference_image_key) <> ''
                  AND m.candidate_id IS NOT NULL
                ON CONFLICT (storage_key) DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "face_images");
        }
    }
}
