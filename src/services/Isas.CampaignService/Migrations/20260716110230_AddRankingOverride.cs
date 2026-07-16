using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddRankingOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "overridden_at",
                table: "campaign_rankings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "overridden_by",
                table: "campaign_rankings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_note",
                table: "campaign_rankings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_result",
                table: "campaign_rankings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "override_score",
                table: "campaign_rankings",
                type: "numeric(5,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "overridden_at",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "overridden_by",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "override_note",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "override_result",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "override_score",
                table: "campaign_rankings");
        }
    }
}
