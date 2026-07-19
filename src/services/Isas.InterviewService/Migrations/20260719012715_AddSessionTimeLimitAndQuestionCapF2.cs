using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionTimeLimitAndQuestionCapF2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "time_limit_sec",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_sessions_max_questions_range",
                table: "practice_sessions",
                sql: "max_questions BETWEEN 0 AND 20");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_sessions_max_questions_range",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "time_limit_sec",
                table: "practice_sessions");
        }
    }
}
