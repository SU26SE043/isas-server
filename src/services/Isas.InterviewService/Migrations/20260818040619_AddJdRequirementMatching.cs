using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddJdRequirementMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "citations",
                table: "cv_analyses",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cv_sections",
                table: "cv_analyses",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requirement_matches",
                table: "cv_analyses",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "citations",
                table: "cv_analyses");

            migrationBuilder.DropColumn(
                name: "cv_sections",
                table: "cv_analyses");

            migrationBuilder.DropColumn(
                name: "requirement_matches",
                table: "cv_analyses");
        }
    }
}
