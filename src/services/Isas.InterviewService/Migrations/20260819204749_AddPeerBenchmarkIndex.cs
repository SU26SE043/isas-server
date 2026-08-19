using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerBenchmarkIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_peer_benchmark",
                table: "practice_sessions",
                columns: new[] { "job_category", "language", "created_at" },
                filter: "campaign_id IS NULL AND status = 'Scored'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_peer_benchmark",
                table: "practice_sessions");
        }
    }
}
