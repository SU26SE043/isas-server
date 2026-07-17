using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddDb5SweeperIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions",
                column: "created_at",
                filter: "campaign_id IS NULL AND deadline IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions",
                column: "deadline",
                filter: "deadline IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_practice_answers_status_lsp",
                table: "practice_answers",
                columns: new[] { "status", "last_scoring_published_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_practice_answers_status_lsp",
                table: "practice_answers");
        }
    }
}
