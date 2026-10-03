using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AllowCameraBlockedFocusSignal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events",
                sql: "signal_type IN ('tab_switch', 'paste', 'focus_lost', 'no_face', 'multiple_faces', 'camera_blocked')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CHECK hẹp lại sẽ bị Postgres kiểm trên dòng đang có ⇒ rollback nổ nếu đã có camera_blocked.
            // Đây là số liệu coaching của buổi luyện (chỉ người luyện đọc), bỏ khi lùi bản là chấp nhận được.
            migrationBuilder.Sql("DELETE FROM practice_focus_events WHERE signal_type = 'camera_blocked';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events",
                sql: "signal_type IN ('tab_switch', 'paste', 'focus_lost', 'no_face', 'multiple_faces')");
        }
    }
}
