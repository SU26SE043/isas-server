using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapMistakesMis1B4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mistake_refs",
                table: "roadmap_milestones",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mistake_refs",
                table: "roadmap_lessons",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mistake_review",
                table: "roadmap_lessons",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "roadmap_mistakes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    roadmap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mistake_key = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    answer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    question = table.Column<string>(type: "text", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    reasoning = table.Column<string>(type: "text", nullable: false),
                    sample_answer = table.Column<string>(type: "text", nullable: true),
                    score_pct = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    threshold_pct = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_mistakes", x => x.id);
                    table.ForeignKey(
                        name: "fk_roadmap_mistakes_practice_answers_answer_id",
                        column: x => x.answer_id,
                        principalTable: "practice_answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_roadmap_mistakes_roadmaps_roadmap_id",
                        column: x => x.roadmap_id,
                        principalTable: "roadmaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_roadmap_mistakes_rubric_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_mistakes_answer_id",
                table: "roadmap_mistakes",
                column: "answer_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_mistakes_criterion_id",
                table: "roadmap_mistakes",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_mistakes_roadmap_id_mistake_key",
                table: "roadmap_mistakes",
                columns: new[] { "roadmap_id", "mistake_key" },
                unique: true);

            // MIS1-B4 — CHECK hình dạng jsonb cho 3 cột array-hoặc-null (mistake_refs × 2 bảng,
            // mistake_review). RAW SQL ở migration, KHÔNG `HasCheckConstraint()` trong Configuration:
            // fluent API nhúng CHECK thẳng vào CREATE TABLE cho MỌI provider kể cả SQLite, mà SQLite
            // không có hàm `jsonb_typeof` ⇒ nổ ngay lúc TestDb.EnsureCreated() dựng schema — tức
            // TOÀN BỘ 1500+ test Interview fail cùng lúc lúc khởi tạo fixture, không phải "mỗi INSERT
            // sẽ lỗi" (tiền lệ F15). Đặt SAU cả 3 AddColumn — đặt trước thì cột chưa tồn tại, ALTER
            // TABLE ADD CONSTRAINT sẽ báo lỗi cột không có, mà SQLite (EnsureCreated bỏ qua migration)
            // không có cách nào bắt lỗi thứ tự này.
            migrationBuilder.Sql(@"
                ALTER TABLE roadmap_milestones
                    ADD CONSTRAINT ck_roadmap_milestones_mistake_refs_array
                    CHECK (mistake_refs IS NULL OR jsonb_typeof(mistake_refs) = 'array');
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE roadmap_lessons
                    ADD CONSTRAINT ck_roadmap_lessons_mistake_refs_array
                    CHECK (mistake_refs IS NULL OR jsonb_typeof(mistake_refs) = 'array');
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE roadmap_lessons
                    ADD CONSTRAINT ck_roadmap_lessons_mistake_review_array
                    CHECK (mistake_review IS NULL OR jsonb_typeof(mistake_review) = 'array');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "roadmap_mistakes");

            migrationBuilder.DropColumn(
                name: "mistake_refs",
                table: "roadmap_milestones");

            migrationBuilder.DropColumn(
                name: "mistake_refs",
                table: "roadmap_lessons");

            migrationBuilder.DropColumn(
                name: "mistake_review",
                table: "roadmap_lessons");
        }
    }
}
