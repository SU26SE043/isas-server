using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignIdToEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "campaign_id",
                table: "rubric_criteria",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_id",
                table: "practice_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_campaign_id",
                table: "rubric_criteria",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_sessions_campaign_id",
                table: "practice_sessions",
                column: "campaign_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rubric_criteria_campaign_id",
                table: "rubric_criteria");

            migrationBuilder.DropIndex(
                name: "ix_practice_sessions_campaign_id",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "campaign_id",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "campaign_id",
                table: "practice_sessions");
        }
    }
}
