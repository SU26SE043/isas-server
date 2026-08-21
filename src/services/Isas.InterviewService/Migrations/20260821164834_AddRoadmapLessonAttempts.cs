using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapLessonAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "roadmap_lesson_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_lesson_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_roadmap_lesson_attempts_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_roadmap_lesson_attempts_roadmap_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "roadmap_lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_lesson_attempts_lesson_id_attempt_no",
                table: "roadmap_lesson_attempts",
                columns: new[] { "lesson_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_lesson_attempts_session_id",
                table: "roadmap_lesson_attempts",
                column: "session_id",
                unique: true);

            // BACKFILL — mọi bài ĐÃ có buổi luyện gắn vào phải có đúng 1 dòng "lần thứ 1". Thiếu nó
            // thì lịch sử khuyết ĐÚNG những bài đã học: `attemptCount` hiện 0 cho bài vừa học xong,
            // và lần làm lại kế tiếp lại được cấp số 1 (trùng nghĩa, không trùng khoá).
            //
            // Phạm vi là `session_id IS NOT NULL`, KHÔNG chỉ riêng bài `Done`: bài đang `Practicing`
            // cũng đã tiêu 1 credit và đã có buổi thật — bỏ qua nhóm đó thì khi buổi ấy được chấm
            // xong, buổi đầu tiên của bài biến mất khỏi đường xu hướng.
            //
            // `created_at` lấy từ chính buổi luyện (thời điểm THẬT của lần làm), không phải now() —
            // now() sẽ dồn mọi lần làm cũ vào một mốc và bóp méo trục thời gian của báo cáo.
            //
            // `DISTINCT ON (l.session_id)`: không ràng buộc nào (trước bản này) chặn hai lesson trỏ
            // chung một buổi; UNIQUE(session_id) của bảng mới thì có, nên phải chốt deterministic
            // MỘT lesson cho mỗi buổi thay vì để migration vỡ giữa chừng ở dữ liệu thật.
            //
            // ⚠ Postgres-only. SQLite/EnsureCreated (test) BỎ QUA migration nên KHÔNG test nào phủ
            // đoạn này — đã đọc bằng mắt: mỗi câu kết thúc bằng ';'.
            migrationBuilder.Sql(@"
                INSERT INTO roadmap_lesson_attempts (id, lesson_id, session_id, attempt_no, created_at)
                SELECT DISTINCT ON (l.session_id)
                       gen_random_uuid(), l.id, l.session_id, 1, ps.created_at
                FROM roadmap_lessons l
                JOIN practice_sessions ps ON ps.id = l.session_id
                WHERE l.session_id IS NOT NULL
                ORDER BY l.session_id, l.id;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DropTable cuốn theo cả dữ liệu backfill lẫn các lần làm mới — không cần dọn riêng.
            // ⚠ Đảo ngược migration này là MẤT lịch sử các lần làm lại (buổi luyện vẫn còn trong
            // practice_sessions; chỉ mất mối nối bài ↔ lần làm thứ 2 trở đi).
            migrationBuilder.DropTable(
                name: "roadmap_lesson_attempts");
        }
    }
}
