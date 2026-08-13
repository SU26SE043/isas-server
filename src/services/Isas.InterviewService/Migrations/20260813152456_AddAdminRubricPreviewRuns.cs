using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminRubricPreviewRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_rubric_preview_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    language = table.Column<string>(type: "text", nullable: false, defaultValue: "vi"),
                    rubric_version = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    rubric_snapshot = table.Column<string>(type: "text", nullable: false),
                    rubric_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    samples = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<int>(type: "integer", nullable: true),
                    length_parity_warning = table.Column<bool>(type: "boolean", nullable: false),
                    error_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_rubric_preview_runs", x => x.id);
                    table.CheckConstraint("ck_admin_rubric_preview_runs_language", "language IN ('vi', 'en')");
                    table.CheckConstraint("ck_admin_rubric_preview_runs_status", "status IN ('Running', 'Succeeded', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_rubric_preview_runs_scope_created",
                table: "admin_rubric_preview_runs",
                columns: new[] { "job_category", "language", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_admin_rubric_preview_runs_running",
                table: "admin_rubric_preview_runs",
                columns: new[] { "job_category", "language", "rubric_version" },
                unique: true,
                filter: "status = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_rubric_preview_runs");
        }
    }
}
