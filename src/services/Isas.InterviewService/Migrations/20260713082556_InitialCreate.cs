using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cv_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cv_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jd_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    strengths = table.Column<string>(type: "jsonb", nullable: false),
                    weaknesses = table.Column<string>(type: "jsonb", nullable: false),
                    suggestions = table.Column<string>(type: "jsonb", nullable: false),
                    jd_match = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cv_analyses", x => x.id);
                });

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
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true)
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
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cv_id = table.Column<Guid>(type: "uuid", nullable: true),
                    jd_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    overall_score = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    answered_count = table.Column<int>(type: "integer", nullable: true),
                    overall_comment = table.Column<string>(type: "text", nullable: true)
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
                name: "roadmaps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cv_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_session_ids = table.Column<string>(type: "jsonb", nullable: true),
                    baseline = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    final_report = table.Column<string>(type: "jsonb", nullable: true),
                    overall_comment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmaps", x => x.id);
                    table.ForeignKey(
                        name: "fk_roadmaps_file_records_cv_id",
                        column: x => x.cv_id,
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
                name: "session_criterion_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    average_score = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    weight = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    needs_improvement = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_criterion_scores", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_criterion_scores_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_session_criterion_scores_rubric_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roadmap_milestones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    roadmap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_no = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    focus_criteria = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    improvement = table.Column<string>(type: "jsonb", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_milestones", x => x.id);
                    table.ForeignKey(
                        name: "fk_roadmap_milestones_roadmaps_roadmap_id",
                        column: x => x.roadmap_id,
                        principalTable: "roadmaps",
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
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    duration_sec = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_scoring_published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                name: "roadmap_lessons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_no = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    theory_content = table.Column<string>(type: "text", nullable: true),
                    theory_generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_lessons", x => x.id);
                    table.ForeignKey(
                        name: "fk_roadmap_lessons_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_roadmap_lessons_roadmap_milestones_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "roadmap_milestones",
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
                    level_matched = table.Column<int>(type: "integer", nullable: true),
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

            migrationBuilder.InsertData(
                table: "rubric_criteria",
                columns: new[] { "id", "campaign_id", "candidate_id", "description", "is_active", "job_category", "max_score", "name", "version", "weight" },
                values: new object[,]
                {
                    { new Guid("0b100000-0000-0000-0000-000000000001"), null, null, "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.", true, "BA", 5, "Phân tích yêu cầu", 1, 0.3000m },
                    { new Guid("0b100000-0000-0000-0000-000000000002"), null, null, "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.", true, "BA", 5, "Giao tiếp & trình bày", 1, 0.2500m },
                    { new Guid("0b100000-0000-0000-0000-000000000003"), null, null, "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.", true, "BA", 5, "Hiểu nghiệp vụ & các bên liên quan", 1, 0.2500m },
                    { new Guid("0b100000-0000-0000-0000-000000000004"), null, null, "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.", true, "BA", 5, "Tư duy giải quyết vấn đề", 1, 0.2000m },
                    { new Guid("0be00000-0000-0000-0000-000000000001"), null, null, "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.", true, "BE", 5, "Chiều sâu kỹ thuật", 1, 0.3000m },
                    { new Guid("0be00000-0000-0000-0000-000000000002"), null, null, "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.", true, "BE", 5, "Thiết kế hệ thống & CSDL", 1, 0.2500m },
                    { new Guid("0be00000-0000-0000-0000-000000000003"), null, null, "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", true, "BE", 5, "Giải quyết vấn đề & thuật toán", 1, 0.2500m },
                    { new Guid("0be00000-0000-0000-0000-000000000004"), null, null, "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.", true, "BE", 5, "Giao tiếp & trình bày", 1, 0.2000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000001"), null, null, "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.", true, "FE", 5, "Chiều sâu kỹ thuật", 1, 0.3000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000002"), null, null, "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.", true, "FE", 5, "Ý thức UI/UX & accessibility", 1, 0.2000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000003"), null, null, "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.", true, "FE", 5, "Giải quyết vấn đề", 1, 0.2500m },
                    { new Guid("0fe00000-0000-0000-0000-000000000004"), null, null, "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.", true, "FE", 5, "Giao tiếp & trình bày", 1, 0.2500m }
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
                name: "ix_cv_analyses_candidate_id",
                table: "cv_analyses",
                column: "candidate_id");

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
                name: "ix_practice_sessions_campaign_id",
                table: "practice_sessions",
                column: "campaign_id");

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
                name: "ix_roadmap_lessons_milestone_id_order_no",
                table: "roadmap_lessons",
                columns: new[] { "milestone_id", "order_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_lessons_session_id",
                table: "roadmap_lessons",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_milestones_roadmap_id_order_no",
                table: "roadmap_milestones",
                columns: new[] { "roadmap_id", "order_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roadmaps_candidate_id",
                table: "roadmaps",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmaps_cv_id",
                table: "roadmaps",
                column: "cv_id");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_anchors_level_id",
                table: "rubric_anchors",
                column: "level_id");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_campaign_id",
                table: "rubric_criteria",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_is_active",
                table: "rubric_criteria",
                columns: new[] { "candidate_id", "job_category", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_job_category_version_is_active",
                table: "rubric_criteria",
                columns: new[] { "job_category", "version", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_rubric_levels_criterion_id_score",
                table: "rubric_levels",
                columns: new[] { "criterion_id", "score" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_criterion_scores_criterion_id",
                table: "session_criterion_scores",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_criterion_scores_session_id_criterion_id",
                table: "session_criterion_scores",
                columns: new[] { "session_id", "criterion_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_scores");

            migrationBuilder.DropTable(
                name: "cv_analyses");

            migrationBuilder.DropTable(
                name: "roadmap_lessons");

            migrationBuilder.DropTable(
                name: "rubric_anchors");

            migrationBuilder.DropTable(
                name: "session_criterion_scores");

            migrationBuilder.DropTable(
                name: "practice_answers");

            migrationBuilder.DropTable(
                name: "roadmap_milestones");

            migrationBuilder.DropTable(
                name: "rubric_levels");

            migrationBuilder.DropTable(
                name: "practice_questions");

            migrationBuilder.DropTable(
                name: "roadmaps");

            migrationBuilder.DropTable(
                name: "rubric_criteria");

            migrationBuilder.DropTable(
                name: "practice_sessions");

            migrationBuilder.DropTable(
                name: "file_records");
        }
    }
}
