using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCheckConstraintsDb15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "campaigns",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_pass_score_pct_range",
                table: "campaigns",
                sql: "pass_score_pct IS NULL OR (pass_score_pct >= 0 AND pass_score_pct <= 100)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_weight_range",
                table: "campaign_criteria",
                sql: "weight > 0 AND weight <= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_pass_score_pct_range",
                table: "campaigns");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_weight_range",
                table: "campaign_criteria");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "campaigns");
        }
    }
}
