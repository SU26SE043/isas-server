using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerSampleAnswerF13 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sample_answer",
                table: "practice_answers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sample_answer",
                table: "practice_answers");
        }
    }
}
