using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRunningSessionCapacityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_running_capacity",
                table: "practice_sessions",
                column: "status",
                filter: "status IN ('GeneratingQuestions', 'Ready', 'InProgress')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_running_capacity",
                table: "practice_sessions");
        }
    }
}
