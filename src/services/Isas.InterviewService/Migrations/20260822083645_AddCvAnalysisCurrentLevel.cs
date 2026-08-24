using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddCvAnalysisCurrentLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "current_level",
                table: "cv_analyses",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cv_analyses_current_level",
                table: "cv_analyses",
                sql: "current_level IS NULL OR current_level IN ('Fresher', 'Junior', 'Middle', 'Senior')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cv_analyses_current_level",
                table: "cv_analyses");

            migrationBuilder.DropColumn(
                name: "current_level",
                table: "cv_analyses");
        }
    }
}
