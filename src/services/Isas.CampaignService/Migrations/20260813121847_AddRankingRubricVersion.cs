using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddRankingRubricVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rubric_version",
                table: "campaign_rankings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rubric_version",
                table: "campaign_rankings");
        }
    }
}
