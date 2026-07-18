using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class ReshapeSweeperAndAddLookupIndexesDb262731 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_candidate_id",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions",
                column: "created_at",
                filter: "status IN ('Ready', 'InProgress') AND campaign_id IS NULL AND deadline IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_candidate_history",
                table: "practice_sessions",
                columns: new[] { "candidate_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions",
                column: "deadline",
                filter: "status = 'InProgress' AND deadline IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_file_records_user_id",
                table: "file_records",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_candidate_history",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions");

            migrationBuilder.DropIndex(
                name: "ix_file_records_user_id",
                table: "file_records");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_b2c_active",
                table: "practice_sessions",
                column: "created_at",
                filter: "campaign_id IS NULL AND deadline IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_candidate_id",
                table: "practice_sessions",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_deadline",
                table: "practice_sessions",
                column: "deadline",
                filter: "deadline IS NOT NULL");
        }
    }
}
