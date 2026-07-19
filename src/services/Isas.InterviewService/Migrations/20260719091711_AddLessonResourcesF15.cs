using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonResourcesF15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F15 — tài liệu học gợi ý (jsonb, non-null).
            //
            // ⚠ EF scaffold ra defaultValue: "" — CHUỖI RỖNG KHÔNG PHẢI JSON HỢP LỆ, Postgres sẽ
            // từ chối ngay tại ALTER TABLE ("invalid input syntax for type json"). Sửa tay thành
            // "[]". SQLite (test, EnsureCreated) BỎ QUA migration nên test KHÔNG bắt được lỗi này —
            // chỉ lộ ra lúc apply Postgres thật.
            migrationBuilder.AddColumn<string>(
                name: "resources",
                table: "roadmap_lessons",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resources",
                table: "roadmap_lessons");
        }
    }
}
