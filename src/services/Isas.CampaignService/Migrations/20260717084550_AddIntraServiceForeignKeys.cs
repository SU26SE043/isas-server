using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddIntraServiceForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_campaign_invitations_campaign_candidate_id",
                table: "campaign_invitations",
                column: "campaign_candidate_id");

            migrationBuilder.AddForeignKey(
                name: "fk_campaign_invitations_campaign_candidates_campaign_candidate",
                table: "campaign_invitations",
                column: "campaign_candidate_id",
                principalTable: "campaign_candidates",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_campaign_rankings_campaigns_campaign_id",
                table: "campaign_rankings",
                column: "campaign_id",
                principalTable: "campaigns",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_session_flags_campaigns_campaign_id",
                table: "session_flags",
                column: "campaign_id",
                principalTable: "campaigns",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_campaign_invitations_campaign_candidates_campaign_candidate",
                table: "campaign_invitations");

            migrationBuilder.DropForeignKey(
                name: "fk_campaign_rankings_campaigns_campaign_id",
                table: "campaign_rankings");

            migrationBuilder.DropForeignKey(
                name: "fk_session_flags_campaigns_campaign_id",
                table: "session_flags");

            migrationBuilder.DropIndex(
                name: "ix_campaign_invitations_campaign_candidate_id",
                table: "campaign_invitations");
        }
    }
}
