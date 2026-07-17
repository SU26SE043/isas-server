using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class SchemaCleanupDb15 : Migration
    {
        // DB15 — dọn schema InterviewService:
        //  (B) bỏ UNIQUE(session_id, question_id) THỪA trên practice_answers, thay bằng index NON-unique
        //      session_id (giữ cột dẫn cho các EXISTS theo session_id; uniqueness "1 answer/câu" đã do
        //      UNIQUE ix_practice_answers_question_id của quan hệ 1-1 lo).
        //  (C) CHECK ck_rubric_criteria_weight_range: weight ∈ (0,1].
        //  (D) GỘP bảng rubric_anchors (1-n) → cột jsonb rubric_levels.example_answers (string[]).
        //      → data-migration TAY: add cột (default '[]') → BACKFILL từ rubric_anchors → DROP bảng.
        // DB10 (Part A) — xmin optimistic concurrency trên practice_sessions: EF sinh AddColumn<uint>
        //      "xmin" nhưng Npgsql SQL generator BỎ QUA (system column) → KHÔNG có DDL thật (đã verify
        //      qua `ef migrations script`). Giữ nguyên để migration khớp delta model.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (D) — thêm cột jsonb array trước, default '[]' (jsonb ARRAY rỗng; KHÔNG '{}' object).
            migrationBuilder.AddColumn<string>(
                name: "example_answers",
                table: "rubric_levels",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            // (D) — BACKFILL: gom câu mẫu của mỗi level thành jsonb array (giữ thứ tự theo id anchor).
            //      Level không có anchor → '[]'. PHẢI chạy TRƯỚC DROP TABLE.
            migrationBuilder.Sql(@"
                UPDATE rubric_levels
                SET example_answers = COALESCE(
                    (SELECT jsonb_agg(a.example_answer ORDER BY a.id)
                     FROM rubric_anchors a
                     WHERE a.level_id = rubric_levels.id),
                    '[]'::jsonb);");

            // (D) — bỏ bảng rubric_anchors sau khi đã backfill.
            migrationBuilder.DropTable(
                name: "rubric_anchors");

            // (B) — bỏ UNIQUE(session_id, question_id) thừa.
            migrationBuilder.DropIndex(
                name: "ix_practice_answers_session_id_question_id",
                table: "practice_answers");

            // (A) — xmin: no-op DDL (Npgsql bỏ qua system column); giữ để faithful với model delta.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "practice_sessions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // (C) — CHECK weight ∈ (0,1].
            migrationBuilder.AddCheckConstraint(
                name: "ck_rubric_criteria_weight_range",
                table: "rubric_criteria",
                sql: "weight > 0 AND weight <= 1");

            // (B) — index NON-unique session_id thay cho composite unique đã drop.
            migrationBuilder.CreateIndex(
                name: "ix_practice_answers_session_id",
                table: "practice_answers",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rubric_criteria_weight_range",
                table: "rubric_criteria");

            migrationBuilder.DropIndex(
                name: "ix_practice_answers_session_id",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "practice_sessions");

            // (D revert) — dựng lại bảng rubric_anchors (PK + FK Cascade + index level_id) như InitialCreate.
            migrationBuilder.CreateTable(
                name: "rubric_anchors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    example_answer = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rubric_anchors", x => x.id);
                    table.ForeignKey(
                        name: "fk_rubric_anchors_rubric_levels_level_id",
                        column: x => x.level_id,
                        principalTable: "rubric_levels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rubric_anchors_level_id",
                table: "rubric_anchors",
                column: "level_id");

            // (D revert) — bung jsonb array → 1 row/phần tử (PK mới; giữ thứ tự phần tử qua ORDINALITY).
            migrationBuilder.Sql(@"
                INSERT INTO rubric_anchors (id, level_id, example_answer)
                SELECT gen_random_uuid(), l.id, elem.value
                FROM rubric_levels l
                CROSS JOIN LATERAL jsonb_array_elements_text(l.example_answers)
                    WITH ORDINALITY AS elem(value, ord)
                ORDER BY l.id, elem.ord;");

            migrationBuilder.DropColumn(
                name: "example_answers",
                table: "rubric_levels");

            migrationBuilder.CreateIndex(
                name: "ix_practice_answers_session_id_question_id",
                table: "practice_answers",
                columns: new[] { "session_id", "question_id" },
                unique: true);
        }
    }
}
