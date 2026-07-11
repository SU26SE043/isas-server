using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddCvAnalyses : Migration
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

            migrationBuilder.CreateIndex(
                name: "ix_cv_analyses_candidate_id",
                table: "cv_analyses",
                column: "candidate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cv_analyses");
        }
    }
}
