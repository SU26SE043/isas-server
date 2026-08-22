using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "roadmaps",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "LevelUp");

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_mode",
                table: "roadmaps",
                sql: "mode IN ('LevelUp', 'Reinforce')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_mode",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "roadmaps");
        }
    }
}
