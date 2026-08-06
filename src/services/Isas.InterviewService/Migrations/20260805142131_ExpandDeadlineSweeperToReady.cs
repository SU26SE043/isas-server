using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class ExpandDeadlineSweeperToReady : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions",
                column: "deadline",
                filter: "status IN ('Ready', 'InProgress') AND deadline IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions",
                column: "deadline",
                filter: "status = 'InProgress' AND deadline IS NOT NULL");
        }
    }
}
