using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRubric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "job_category",
                table: "rubric_criteria",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "rubric_criteria",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_job_category_version_is_active",
                table: "rubric_criteria",
                columns: new[] { "job_category", "version", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rubric_criteria_job_category_version_is_active",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "job_category",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "version",
                table: "rubric_criteria");
        }
    }
}
