using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRepoAnalyses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repo_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_url = table.Column<string>(type: "text", nullable: false),
                    repo_owner = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    repo_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    stars = table.Column<int>(type: "integer", nullable: false),
                    primary_language = table.Column<string>(type: "text", nullable: true),
                    languages = table.Column<string>(type: "jsonb", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    tech_stack = table.Column<string>(type: "jsonb", nullable: false),
                    strengths = table.Column<string>(type: "jsonb", nullable: false),
                    weaknesses = table.Column<string>(type: "jsonb", nullable: false),
                    suggestions = table.Column<string>(type: "jsonb", nullable: false),
                    interview_talking_points = table.Column<string>(type: "jsonb", nullable: false),
                    jd_match = table.Column<string>(type: "jsonb", nullable: true),
                    jd_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repo_analyses", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_repo_analyses_candidate_id",
                table: "repo_analyses",
                column: "candidate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repo_analyses");
        }
    }
}
