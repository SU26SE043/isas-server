using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "practice_topics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    job_category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    seniority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    language = table.Column<string>(type: "text", nullable: false, defaultValue: "vi"),
                    label = table.Column<string>(type: "text", nullable: false),
                    criterion_name = table.Column<string>(type: "text", nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_topics", x => x.id);
                    table.CheckConstraint("ck_practice_topics_seniority", "seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_practice_topics_lookup",
                table: "practice_topics",
                columns: new[] { "job_category", "seniority", "language", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_practice_topics_key_language_version",
                table: "practice_topics",
                columns: new[] { "topic_key", "language", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_topics");
        }
    }
}
