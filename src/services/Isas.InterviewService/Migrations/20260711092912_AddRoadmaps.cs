using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "roadmap_lessons");

            migrationBuilder.DropTable(
                name: "roadmap_milestones");

            migrationBuilder.DropTable(
                name: "roadmaps");
        }
    }
}
