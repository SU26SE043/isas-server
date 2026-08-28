using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapMistakeSeniorityRec1B2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "seniority",
                table: "roadmap_mistakes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmap_mistakes_seniority",
                table: "roadmap_mistakes",
                sql: "seniority IS NULL OR seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmap_mistakes_seniority",
                table: "roadmap_mistakes");

            migrationBuilder.DropColumn(
                name: "seniority",
                table: "roadmap_mistakes");
        }
    }
}
