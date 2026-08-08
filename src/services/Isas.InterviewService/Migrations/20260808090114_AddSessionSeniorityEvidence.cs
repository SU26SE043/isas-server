using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSeniorityEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "seniority",
                table: "practice_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Junior");

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_sessions_seniority",
                table: "practice_sessions",
                sql: "seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_sessions_seniority",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "seniority",
                table: "practice_sessions");
        }
    }
}
