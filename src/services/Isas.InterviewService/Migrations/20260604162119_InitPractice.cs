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
                name: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    original_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    storage_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    storage_bucket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    parsed_text = table.Column<string>(type: "text", nullable: true),
                    parse_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "practice_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_category = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "draft"),
                    cv_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    jd_text = table.Column<string>(type: "text", nullable: true),
                    total_score = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    feedback = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    scored_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_practice_sessions_files_cv_file_id",
                        column: x => x.cv_file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "practice_questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_questions", x => x.id);
                    table.ForeignKey(
                        name: "FK_practice_questions_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "practice_answers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answer_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    text_content = table.Column<string>(type: "text", nullable: true),
                    audio_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    feedback = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_answers", x => x.id);
                    table.ForeignKey(
                        name: "FK_practice_answers_files_audio_file_id",
                        column: x => x.audio_file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_practice_answers_practice_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "practice_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_files_parse_status",
                table: "files",
                column: "parse_status");

            migrationBuilder.CreateIndex(
                name: "idx_files_user",
                table: "files",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_files_user_type",
                table: "files",
                columns: new[] { "user_id", "file_type" });

            migrationBuilder.CreateIndex(
                name: "idx_practice_a_question",
                table: "practice_answers",
                column: "question_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_practice_a_session",
                table: "practice_answers",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_practice_answers_audio_file_id",
                table: "practice_answers",
                column: "audio_file_id");

            migrationBuilder.CreateIndex(
                name: "idx_practice_q_order",
                table: "practice_questions",
                columns: new[] { "session_id", "order_index" });

            migrationBuilder.CreateIndex(
                name: "idx_practice_q_session",
                table: "practice_questions",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "idx_practice_status",
                table: "practice_sessions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_practice_user",
                table: "practice_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_practice_sessions_cv_file_id",
                table: "practice_sessions",
                column: "cv_file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_answers");

            migrationBuilder.DropTable(
                name: "practice_questions");

            migrationBuilder.DropTable(
                name: "practice_sessions");

            migrationBuilder.DropTable(
                name: "files");
        }
    }
}
