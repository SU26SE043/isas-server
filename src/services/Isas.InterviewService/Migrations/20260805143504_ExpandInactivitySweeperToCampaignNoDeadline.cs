using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class ExpandInactivitySweeperToCampaignNoDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions",
                column: "created_at",
                filter: "status IN ('Ready', 'InProgress') AND deadline IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions",
                column: "created_at",
                filter: "status IN ('Ready', 'InProgress') AND campaign_id IS NULL AND deadline IS NULL");
        }
    }
}
