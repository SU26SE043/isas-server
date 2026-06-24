using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class InitPractice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_type = table.Column<string>(type: "text", nullable: false),
                    original_name = table.Column<string>(type: "text", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    storage_bucket = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    parsed_text = table.Column<string>(type: "text", nullable: true),
                    parse_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rubric_criteria",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rubric_criteria", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "practice_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cv_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jd_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_practice_sessions_file_records_cv_id",
                        column: x => x.cv_id,
                        principalTable: "file_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_practice_sessions_file_records_jd_id",
                        column: x => x.jd_id,
                        principalTable: "file_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rubric_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    descriptor = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rubric_levels", x => x.id);
                    table.ForeignKey(
                        name: "fk_rubric_levels_rubric_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "practice_questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_no = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    time_limit_sec = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_questions", x => x.id);
                    table.ForeignKey(
                        name: "fk_practice_questions_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "practice_answers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audio_object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    transcript = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    duration_sec = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_answers", x => x.id);
                    table.ForeignKey(
                        name: "fk_practice_answers_practice_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "practice_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_practice_answers_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "answer_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    answer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    reasoning = table.Column<string>(type: "text", nullable: true),
                    rubric_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answer_scores", x => x.id);
                    table.ForeignKey(
                        name: "fk_answer_scores_practice_answers_answer_id",
                        column: x => x.answer_id,
                        principalTable: "practice_answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_answer_scores_rubric_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_answer_scores_answer_id_criterion_id_attempt_no",
                table: "answer_scores",
                columns: new[] { "answer_id", "criterion_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_answer_scores_criterion_id",
                table: "answer_scores",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_answers_question_id",
                table: "practice_answers",
                column: "question_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_practice_answers_session_id_question_id",
                table: "practice_answers",
                columns: new[] { "session_id", "question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_practice_questions_session_id_order_no",
                table: "practice_questions",
                columns: new[] { "session_id", "order_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_candidate_id",
                table: "practice_sessions",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_cv_id",
                table: "practice_sessions",
                column: "cv_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_jd_id",
                table: "practice_sessions",
                column: "jd_id");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_anchors_level_id",
                table: "rubric_anchors",
                column: "level_id");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_levels_criterion_id_score",
                table: "rubric_levels",
                columns: new[] { "criterion_id", "score" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_scores");

            migrationBuilder.DropTable(
                name: "rubric_anchors");

            migrationBuilder.DropTable(
                name: "practice_answers");

            migrationBuilder.DropTable(
                name: "rubric_levels");

            migrationBuilder.DropTable(
                name: "practice_questions");

            migrationBuilder.DropTable(
                name: "rubric_criteria");

            migrationBuilder.DropTable(
                name: "practice_sessions");

            migrationBuilder.DropTable(
                name: "file_records");
        }
    }
}
