using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCampaignPolicyPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "campaign_policy_engine_version",
                table: "practice_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "campaign_policy_expression",
                table: "practice_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "campaign_policy_pass_score_pct",
                table: "practice_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "campaign_policy_version",
                table: "practice_sessions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "campaign_policy_engine_version",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "campaign_policy_expression",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "campaign_policy_pass_score_pct",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "campaign_policy_version",
                table: "practice_sessions");
        }
    }
}
