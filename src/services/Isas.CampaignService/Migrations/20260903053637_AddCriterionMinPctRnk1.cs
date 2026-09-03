using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCriterionMinPctRnk1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "min_pct",
                table: "campaign_criteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_min_pct_range",
                table: "campaign_criteria",
                sql: "min_pct IS NULL OR (min_pct >= 0 AND min_pct <= 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_min_pct_range",
                table: "campaign_criteria");

            migrationBuilder.DropColumn(
                name: "min_pct",
                table: "campaign_criteria");
        }
    }
}
