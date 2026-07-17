using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// DB16 — Tách bảng God <c>campaign_candidates</c> thành:
    ///  • <c>cv_submission</c> = RENAME của campaign_candidates (giữ PK + toàn bộ id/dữ liệu + CẢ 2 FK
    ///    inbound: candidate_criterion_scores.candidate_id Cascade, campaign_invitations.campaign_candidate_id
    ///    SetNull) − 5 cột membership.
    ///  • <c>campaign_membership</c> = bảng MỚI (candidate_id/joined_at/session_id/interview_status/
    ///    reference_image_key), backfill từ các dòng campaign_candidates có dữ liệu membership.
    ///
    /// Chiến lược RENAME (không drop-create) để KHÔNG mất dữ liệu + không phá 2 FK inbound.
    /// ⚠ KHÔNG chạy trên SQLite test (EnsureCreated bỏ qua migration) → chỉ verify Postgres ở L3.
    /// ⚠ Áp thật (L3): backfill dưới đây tự chuẩn hoá <c>status='Joined'</c> (giá trị cũ trên bảng God,
    ///    KHÔNG còn trong CvSubmissionStatus) → <c>'Pending'</c> để EF đọc lại hợp lệ.
    /// </summary>
    public partial class SplitCampaignCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Bảng membership MỚI (FK cv_submission_id hoãn tới bước 6 — cv_submission chưa tồn tại).
            migrationBuilder.CreateTable(
                name: "campaign_membership",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cv_submission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Joined"),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    interview_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reference_image_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_membership", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_membership_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_campaign_id_candidate_id",
                table: "campaign_membership",
                columns: new[] { "campaign_id", "candidate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_candidate_id",
                table: "campaign_membership",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_cv_submission_id",
                table: "campaign_membership",
                column: "cv_submission_id",
                unique: true);

            // 2) Backfill: mỗi dòng campaign_candidates có dữ liệu membership → 1 row membership,
            //    cv_submission_id = id (dòng đó — sau rename thành cv_submission.id, FK bước 6 thoả).
            migrationBuilder.Sql(@"
                INSERT INTO campaign_membership
                    (id, campaign_id, cv_submission_id, candidate_id, status, joined_at, session_id, interview_status, reference_image_key, created_at, updated_at)
                SELECT gen_random_uuid(), campaign_id, id, candidate_id, 'Joined', COALESCE(joined_at, created_at), session_id, interview_status, reference_image_key, created_at, now()
                FROM campaign_candidates
                WHERE candidate_id IS NOT NULL OR joined_at IS NOT NULL OR session_id IS NOT NULL;");

            // 3) RENAME bảng — giữ PK, mọi dữ liệu, và CẢ 2 FK inbound (theo OID, tên cũ tạm giữ).
            migrationBuilder.RenameTable(name: "campaign_candidates", newName: "cv_submission");

            // 4) Bỏ index + 5 cột membership khỏi cv_submission (đã tách sang campaign_membership).
            migrationBuilder.DropIndex(name: "ix_campaign_candidates_candidate_id", table: "cv_submission");
            migrationBuilder.DropColumn(name: "candidate_id", table: "cv_submission");
            migrationBuilder.DropColumn(name: "joined_at", table: "cv_submission");
            migrationBuilder.DropColumn(name: "session_id", table: "cv_submission");
            migrationBuilder.DropColumn(name: "interview_status", table: "cv_submission");
            migrationBuilder.DropColumn(name: "reference_image_key", table: "cv_submission");

            // 4.5) Chuẩn hoá status='Joined' (giá trị cũ trên bảng God — KHÔNG còn trong CvSubmissionStatus).
            //      "Đã join" nay biểu diễn bằng row campaign_membership; các dòng CV này về Pending (hợp lệ).
            migrationBuilder.Sql("UPDATE cv_submission SET status = 'Pending' WHERE status = 'Joined';");

            // 5) Đổi tên PK/FK/index còn sống cho khớp snapshot (cv_submission + 2 inbound FK).
            migrationBuilder.Sql(@"ALTER TABLE cv_submission RENAME CONSTRAINT ""pk_campaign_candidates"" TO ""pk_cv_submission"";");
            migrationBuilder.Sql(@"ALTER TABLE cv_submission RENAME CONSTRAINT ""fk_campaign_candidates_campaigns_campaign_id"" TO ""fk_cv_submission_campaigns_campaign_id"";");
            migrationBuilder.RenameIndex(name: "ix_campaign_candidates_campaign_id_email", newName: "ix_cv_submission_campaign_id_email", table: "cv_submission");
            migrationBuilder.RenameIndex(name: "ix_campaign_candidates_campaign_id_status", newName: "ix_cv_submission_campaign_id_status", table: "cv_submission");
            migrationBuilder.RenameIndex(name: "ix_campaign_candidates_status_lsp", newName: "ix_cv_submission_status_lsp", table: "cv_submission");
            migrationBuilder.Sql(@"ALTER TABLE candidate_criterion_scores RENAME CONSTRAINT ""fk_candidate_criterion_scores_campaign_candidates_candidate_id"" TO ""fk_candidate_criterion_scores_cv_submission_candidate_id"";");
            migrationBuilder.Sql(@"ALTER TABLE campaign_invitations RENAME CONSTRAINT ""fk_campaign_invitations_campaign_candidates_campaign_candidate"" TO ""fk_campaign_invitations_cv_submission_campaign_candidate_id"";");

            // 6) FK membership.cv_submission_id → cv_submission(id) (SetNull) — sau khi cv_submission đã đủ hình.
            migrationBuilder.AddForeignKey(
                name: "fk_campaign_membership_cv_submission_cv_submission_id",
                table: "campaign_membership",
                column: "cv_submission_id",
                principalTable: "cv_submission",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Đảo ngược: cv_submission → campaign_candidates + gộp membership trở lại (best-effort).
            // ⚠ path-1 (cv_submission_id IS NULL) reconstruct KHÔNG byte-identical (chỉ giữ được membership
            //   fields, status/parse_status='Pending'); status='Joined' cũ đã mất ở Up (không đảo lại được).

            // 6') Bỏ FK membership → cv_submission.
            migrationBuilder.DropForeignKey(
                name: "fk_campaign_membership_cv_submission_cv_submission_id",
                table: "campaign_membership");

            // 5') Đổi tên PK/FK/index về campaign_candidates (bảng vẫn đang tên cv_submission).
            migrationBuilder.Sql(@"ALTER TABLE campaign_invitations RENAME CONSTRAINT ""fk_campaign_invitations_cv_submission_campaign_candidate_id"" TO ""fk_campaign_invitations_campaign_candidates_campaign_candidate"";");
            migrationBuilder.Sql(@"ALTER TABLE candidate_criterion_scores RENAME CONSTRAINT ""fk_candidate_criterion_scores_cv_submission_candidate_id"" TO ""fk_candidate_criterion_scores_campaign_candidates_candidate_id"";");
            migrationBuilder.RenameIndex(name: "ix_cv_submission_status_lsp", newName: "ix_campaign_candidates_status_lsp", table: "cv_submission");
            migrationBuilder.RenameIndex(name: "ix_cv_submission_campaign_id_status", newName: "ix_campaign_candidates_campaign_id_status", table: "cv_submission");
            migrationBuilder.RenameIndex(name: "ix_cv_submission_campaign_id_email", newName: "ix_campaign_candidates_campaign_id_email", table: "cv_submission");
            migrationBuilder.Sql(@"ALTER TABLE cv_submission RENAME CONSTRAINT ""fk_cv_submission_campaigns_campaign_id"" TO ""fk_campaign_candidates_campaigns_campaign_id"";");
            migrationBuilder.Sql(@"ALTER TABLE cv_submission RENAME CONSTRAINT ""pk_cv_submission"" TO ""pk_campaign_candidates"";");

            // 4') Trả lại 5 cột membership lên cv_submission (nullable) + index candidate_id.
            migrationBuilder.AddColumn<Guid>(name: "candidate_id", table: "cv_submission", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "joined_at", table: "cv_submission", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "session_id", table: "cv_submission", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "interview_status", table: "cv_submission", type: "character varying(16)", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "reference_image_key", table: "cv_submission", type: "text", nullable: true);

            // 2') Gộp dữ liệu membership trở lại. path-2 (có cv_submission_id) → UPDATE row CV cũ;
            //     path-1 (cv_submission_id NULL, membership NEW-code) → INSERT row CV tối thiểu reconstruct.
            migrationBuilder.Sql(@"
                UPDATE cv_submission c
                SET candidate_id = m.candidate_id,
                    joined_at = m.joined_at,
                    session_id = m.session_id,
                    interview_status = m.interview_status,
                    reference_image_key = m.reference_image_key
                FROM campaign_membership m
                WHERE m.cv_submission_id = c.id;");
            migrationBuilder.Sql(@"
                INSERT INTO cv_submission
                    (id, campaign_id, candidate_id, status, parse_status, joined_at, session_id, interview_status, reference_image_key, created_at, updated_at)
                SELECT m.id, m.campaign_id, m.candidate_id, 'Pending', 'Pending', m.joined_at, m.session_id, m.interview_status, m.reference_image_key, m.created_at, now()
                FROM campaign_membership m
                WHERE m.cv_submission_id IS NULL;");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_candidates_candidate_id",
                table: "cv_submission",
                column: "candidate_id");

            // 3') Đổi tên bảng về campaign_candidates.
            migrationBuilder.RenameTable(name: "cv_submission", newName: "campaign_candidates");

            // 1') Bỏ bảng membership.
            migrationBuilder.DropTable(name: "campaign_membership");
        }
    }
}
