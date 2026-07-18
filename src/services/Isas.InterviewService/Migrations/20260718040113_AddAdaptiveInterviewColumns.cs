using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddAdaptiveInterviewColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "adaptive_enabled",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "max_follow_ups",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "max_questions",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "generated_from_answer_id",
                table: "practice_questions",
                type: "uuid",
                nullable: true);

            // Backfill rows cũ: mọi câu hỏi hiện có là câu mở đầu → 'Seed' (không phải "" mặc định EF sinh).
            // Postgres ADD COLUMN ... DEFAULT 'Seed' NOT NULL điền luôn rows cũ trong 1 lệnh (không cần UPDATE).
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "practice_questions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Seed");

            migrationBuilder.CreateIndex(
                name: "ix_practice_questions_generated_from_answer_id",
                table: "practice_questions",
                column: "generated_from_answer_id",
                unique: true,
                filter: "generated_from_answer_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_questions_generated_from_answer_id",
                table: "practice_questions");

            migrationBuilder.DropColumn(
                name: "adaptive_enabled",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "max_follow_ups",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "max_questions",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "generated_from_answer_id",
                table: "practice_questions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "practice_questions");
        }
    }
}
