using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionChainDepthInt17b : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "adaptive_failures",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "max_deep_per_question",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "depth",
                table: "practice_questions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "root_question_id",
                table: "practice_questions",
                type: "uuid",
                nullable: true);

            // INT-17b — BACKFILL BẮT BUỘC, không phải cho đẹp: production đang có buổi adaptive chạy dở.
            // Để `depth` ở mặc định 0 thì mọi câu ĐÃ đào sâu sẽ bị coi là câu gốc ⇒ chuỗi đang chạy được
            // đào thêm 3 tầng nữa (vượt trần một cách âm thầm).
            //
            // Dựng lại cây từ `generated_from_answer_id` (câu con) → `practice_answers.question_id` (câu cha):
            //   - seed  = generated_from_answer_id IS NULL → depth 0, root_question_id NULL (null ⇔ tự nó là gốc)
            //   - con   = depth cha + 1, thừa kế root của cha
            //
            // ⚠ SQLite/`EnsureCreated` (bộ test) KHÔNG BAO GIỜ chạy migration ⇒ câu SQL này không có
            // test nào phủ. Phải verify trên Postgres throwaway trước khi apply thật (mẫu L3 của DB15).
            // ⚠ PHẢI kết thúc bằng `;` — thiếu dấu này làm vỡ idempotent script lúc deploy dù
            // `dotnet ef database update` vẫn chạy được (bài học `AddAuditColumnsAndTypes`).
            migrationBuilder.Sql("""
                WITH RECURSIVE chain(id, root_id, lvl) AS (
                    SELECT q.id, q.id, 0
                    FROM practice_questions q
                    WHERE q.generated_from_answer_id IS NULL
                    UNION ALL
                    SELECT c.id, chain.root_id, chain.lvl + 1
                    FROM practice_questions c
                    JOIN practice_answers a ON a.id = c.generated_from_answer_id
                    JOIN chain ON chain.id = a.question_id
                )
                UPDATE practice_questions q
                SET depth = chain.lvl,
                    root_question_id = chain.root_id
                FROM chain
                WHERE q.id = chain.id
                  AND q.generated_from_answer_id IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_practice_questions_session_id_root_question_id_depth",
                table: "practice_questions",
                columns: new[] { "session_id", "root_question_id", "depth" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_questions_session_id_root_question_id_depth",
                table: "practice_questions");

            migrationBuilder.DropColumn(
                name: "adaptive_failures",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "max_deep_per_question",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "depth",
                table: "practice_questions");

            migrationBuilder.DropColumn(
                name: "root_question_id",
                table: "practice_questions");
        }
    }
}
