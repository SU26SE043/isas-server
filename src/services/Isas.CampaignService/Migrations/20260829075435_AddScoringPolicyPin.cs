using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddScoringPolicyPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "scoring_policy_version",
                table: "cv_submission",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scoring_inputs",
                table: "campaign_rankings",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scoring_policy_version",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "scoring_inputs",
                table: "campaign_rankings");
        }
    }
}
