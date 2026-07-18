using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddDb26Indexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_campaigns_org_id_created_at",
                table: "campaigns");

            migrationBuilder.DropIndex(
                name: "ix_campaign_rankings_campaign_id_total_score",
                table: "campaign_rankings");

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_org_id_created_at_id",
                table: "campaigns",
                columns: new[] { "org_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_rankings_campaign_id",
                table: "campaign_rankings",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_session_id",
                table: "campaign_membership",
                column: "session_id",
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_user_id_at",
                table: "audit_logs",
                columns: new[] { "actor_user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_org_id_at",
                table: "audit_logs",
                columns: new[] { "org_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_campaigns_org_id_created_at_id",
                table: "campaigns");

            migrationBuilder.DropIndex(
                name: "ix_campaign_rankings_campaign_id",
                table: "campaign_rankings");

            migrationBuilder.DropIndex(
                name: "ix_campaign_membership_session_id",
                table: "campaign_membership");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_actor_user_id_at",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_org_id_at",
                table: "audit_logs");

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_org_id_created_at",
                table: "campaigns",
                columns: new[] { "org_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_rankings_campaign_id_total_score",
                table: "campaign_rankings",
                columns: new[] { "campaign_id", "total_score" });
        }
    }
}
