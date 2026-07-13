using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class DropCampaignRankingRankResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rank",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "result",
                table: "campaign_rankings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rank",
                table: "campaign_rankings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result",
                table: "campaign_rankings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }
    }
}
