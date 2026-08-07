using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "roadmaps",
                type: "text",
                nullable: false,
                defaultValue: "vi");

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_language",
                table: "roadmaps",
                sql: "language IN ('vi', 'en')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_language",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "language",
                table: "roadmaps");
        }
    }
}
