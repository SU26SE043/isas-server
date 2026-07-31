using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeAndGroundingRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "grounding_refs",
                table: "roadmap_lessons",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "grounding_refs",
                table: "practice_questions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    source_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_ref = table.Column<string>(type: "text", nullable: true),
                    raw_content = table.Column<string>(type: "text", nullable: true),
                    reputation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_sources", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_sources_created",
                table: "knowledge_sources",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_sources_job_category",
                table: "knowledge_sources",
                column: "job_category");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "grounding_refs",
                table: "roadmap_lessons");

            migrationBuilder.DropColumn(
                name: "grounding_refs",
                table: "practice_questions");
        }
    }
}
