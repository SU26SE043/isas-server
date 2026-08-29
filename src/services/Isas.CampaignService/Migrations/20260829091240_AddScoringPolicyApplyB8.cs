using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddScoringPolicyApplyB8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

            migrationBuilder.AddColumn<string>(
                name: "policy_name",
                table: "campaign_rankings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "policy_version",
                table: "campaign_rankings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "score_fallback",
                table: "campaign_rankings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey', 'ApplyScoringPolicy')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "policy_name",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "policy_version",
                table: "campaign_rankings");

            migrationBuilder.DropColumn(
                name: "score_fallback",
                table: "campaign_rankings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey')");
        }
    }
}
