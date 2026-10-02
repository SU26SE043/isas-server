using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionDurationAndBegunAtAtt1 : Migration
    {
        // ATT1 — hai cột NULLABLE, KHÔNG default, KHÔNG backfill (thuần additive):
        //   • practice_sessions.duration_minutes: thời lượng cả buổi (phút) HR đặt, ghim lúc tạo buổi B2B.
        //     null = không tính giờ — đúng nghĩa cho MỌI row cũ (B2C + B2B trước ATT1) ⇒ buổi đang chạy
        //     lúc deploy giữ nguyên hành vi. CHECK chặn giá trị ngoài 5..180 ở tầng DB.
        //   • practice_sessions.begun_at: mốc vào phòng thi (begin). null = chưa vào phòng / không tính giờ.
        // Code cũ không đọc hai cột này ⇒ apply TRƯỚC deploy an toàn.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "begun_at",
                table: "practice_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "duration_minutes",
                table: "practice_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_sessions_duration_minutes_range",
                table: "practice_sessions",
                sql: "duration_minutes IS NULL OR duration_minutes BETWEEN 5 AND 180");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_sessions_duration_minutes_range",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "begun_at",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "duration_minutes",
                table: "practice_sessions");
        }
    }
}
