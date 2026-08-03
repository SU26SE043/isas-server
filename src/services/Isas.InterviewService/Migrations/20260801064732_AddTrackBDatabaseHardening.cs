using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackBDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_sessions_status",
                table: "practice_sessions",
                sql: "status IN ('GeneratingQuestions', 'Ready', 'InProgress', 'Completed', 'Scoring', 'Scored', 'Failed', 'SessionAbandoned')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_answers_status",
                table: "practice_answers",
                sql: "status IN ('Uploaded', 'Transcribing', 'Transcribed', 'Scoring', 'Scored', 'Skipped', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_sessions_status",
                table: "practice_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_answers_status",
                table: "practice_answers");
        }
    }
}
