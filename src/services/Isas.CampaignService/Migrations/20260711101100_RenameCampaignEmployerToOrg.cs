using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class RenameCampaignEmployerToOrg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "employer_id",
                table: "campaigns",
                newName: "org_id");

            migrationBuilder.RenameIndex(
                name: "ix_campaigns_employer_id_status",
                table: "campaigns",
                newName: "ix_campaigns_org_id_status");

            migrationBuilder.RenameIndex(
                name: "ix_campaigns_employer_id_created_at",
                table: "campaigns",
                newName: "ix_campaigns_org_id_created_at");

            migrationBuilder.RenameColumn(
                name: "employer_id",
                table: "campaign_questions",
                newName: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "org_id",
                table: "campaigns",
                newName: "employer_id");

            migrationBuilder.RenameIndex(
                name: "ix_campaigns_org_id_status",
                table: "campaigns",
                newName: "ix_campaigns_employer_id_status");

            migrationBuilder.RenameIndex(
                name: "ix_campaigns_org_id_created_at",
                table: "campaigns",
                newName: "ix_campaigns_employer_id_created_at");

            migrationBuilder.RenameColumn(
                name: "org_id",
                table: "campaign_questions",
                newName: "employer_id");
        }
    }
}
