using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// SC2 · W1 — Campaign biết "tiêu chí này chấm ở mọi câu hay chỉ khi câu nhắm tới" và "câu này nhắm
    /// tiêu chí nào" (chấm theo phạm vi câu hỏi cho B2B, tiền đề INT-18 mở rộng từ B2C).
    ///
    /// <list type="bullet">
    /// <item><c>campaign_criteria.scoring_scope varchar(16) NOT NULL DEFAULT 'Always'</c> + CHECK đóng
    /// <c>('Always','WhenTargeted')</c>. Hàng cũ nhận <c>'Always'</c> qua DEFAULT = hành vi trước SC2
    /// (mọi tiêu chí chấm mọi câu) — KHÔNG backfill, không đổi điểm ai.</item>
    /// <item><c>campaign_questions.target_criterion_ids jsonb NULL</c>, KHÔNG default: <c>null</c>
    /// ("chưa gắn nhãn" ⇒ Interview chấm đủ bộ) khác <c>[]</c> ("đã xét, không nhắm" ⇒ chỉ Always); DB
    /// default sẽ xoá phân biệt đó. Cũng né bug F15 (<c>defaultValue: ""</c> trên jsonb làm Postgres từ
    /// chối ALTER TABLE, SQLite thì bỏ qua migration nên test xanh 100%).</item>
    /// <item>Nới CHECK <c>ck_audit_logs_action</c> thêm <c>'ClearQuestionTargets'</c> (from-system-default
    /// mint id tiêu chí mới ⇒ nhãn mọi câu về null, ghi audit riêng). Khuôn <c>AddAuditActionStartEarlyCmp3B4</c>.</item>
    /// </list>
    ///
    /// <para>Thuần additive, 0 <c>Sql()</c>, 0 <c>UpdateData</c> — đọc bằng mắt sau khi scaffold.</para>
    ///
    /// <para>🔴 THỨ TỰ BẮT BUỘC: apply TRƯỚC hoặc CÙNG LÚC deploy code. Code lên trước ⇒ mọi đường
    /// nạp nguyên entity <c>campaign_criteria</c>/<c>campaign_questions</c> ăn <c>42703 column does not
    /// exist</c> (đã xảy ra 3 lần trong repo). Chiều ngược lại (migration trước, code cũ) vô hại: cột mới
    /// nằm im với DEFAULT/NULL.</para>
    ///
    /// <para>🔴 <see cref="Down"/> LÀ CỬA MỘT CHIỀU ở vế audit: dựng lại CHECK KHÔNG có
    /// <c>'ClearQuestionTargets'</c>, nên đã có row <c>action = 'ClearQuestionTargets'</c> thì
    /// <c>AddCheckConstraint</c> NÉM (<c>23514</c>) — xoá các row đó trước nếu thật sự cần rollback.
    /// Hai cột mới drop sạch (mất nhãn + phạm vi, chấp nhận khi rollback).</para>
    ///
    /// <para>KHÔNG tự chạy <c>dotnet ef database update</c> lên DB chung — chỉ commit file này; người vận
    /// hành apply theo pipeline/tay.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddScoringScopeAndQuestionTargetsSc2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

            migrationBuilder.AddColumn<string>(
                name: "target_criterion_ids",
                table: "campaign_questions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scoring_scope",
                table: "campaign_criteria",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Always");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_scoring_scope",
                table: "campaign_criteria",
                sql: "scoring_scope IN ('Always', 'WhenTargeted')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey', 'ApplyScoringPolicy', 'StartEarly', 'ClearQuestionTargets')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_scoring_scope",
                table: "campaign_criteria");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "target_criterion_ids",
                table: "campaign_questions");

            migrationBuilder.DropColumn(
                name: "scoring_scope",
                table: "campaign_criteria");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey', 'ApplyScoringPolicy', 'StartEarly')");
        }
    }
}
