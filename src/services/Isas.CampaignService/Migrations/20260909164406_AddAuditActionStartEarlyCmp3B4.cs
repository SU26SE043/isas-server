using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// CMP3-B4 — nới CHECK <c>ck_audit_logs_action</c> để nhận giá trị mới <c>'StartEarly'</c>
    /// (POST /campaign/{id}/start-now ghi audit). Thuần drop + add lại CHECK — KHÔNG <c>Sql()</c>
    /// thô, KHÔNG DDL bảng. Khuôn giống <c>AddScoringPolicyApplyB8</c>.
    ///
    /// <para>🔴 THỨ TỰ BẮT BUỘC: apply migration này TRƯỚC hoặc CÙNG LÚC deploy code. Code lên
    /// trước ⇒ mọi lần bấm "Bắt đầu sớm" ăn <c>23514 check_violation</c> (giá trị <c>'StartEarly'</c>
    /// chưa nằm trong CHECK). Chiều ngược lại (migration trước, code sau) vô hại. Repo đã dính
    /// "code đi trước migration" ba lần.</para>
    ///
    /// <para>🔴 <see cref="Down"/> LÀ CỬA MỘT CHIỀU: nó dựng lại CHECK KHÔNG có <c>'StartEarly'</c>,
    /// nên nếu <c>audit_logs</c> đã có row <c>action = 'StartEarly'</c> thì <c>AddCheckConstraint</c>
    /// sẽ NÉM (<c>23514</c>). Muốn rollback thật phải xoá các row đó trước — coi migration này như
    /// không rollback được sau khi tính năng đã chạy.</para>
    ///
    /// <para>KHÔNG tự chạy <c>dotnet ef database update</c> lên DB chung — chỉ commit file này;
    /// người vận hành apply theo pipeline/tay.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddAuditActionStartEarlyCmp3B4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey', 'ApplyScoringPolicy', 'StartEarly')");
        }

        /// <summary>
        /// ⚠ CỬA MỘT CHIỀU — dựng lại CHECK cũ (không có 'StartEarly'). NÉM nếu đã tồn tại row
        /// <c>action = 'StartEarly'</c>. Xem chú thích class.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey', 'ApplyScoringPolicy')");
        }
    }
}
