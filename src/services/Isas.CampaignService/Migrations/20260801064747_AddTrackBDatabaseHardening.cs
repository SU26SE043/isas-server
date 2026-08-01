using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackBDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_cv_submission_parse_status",
                table: "cv_submission",
                sql: "parse_status IN ('Pending', 'Done', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cv_submission_status",
                table: "cv_submission",
                sql: "status IN ('Pending', 'Filtered', 'Rejected', 'Analyzing', 'Analyzed', 'AnalysisFailed', 'Invited')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_status",
                table: "campaigns",
                sql: "status IN ('Draft', 'Active', 'Closed', 'Archived')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_questions_source",
                table: "campaign_questions",
                sql: "source IN ('AiGenerated', 'CustomHr')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_membership_interview_status",
                table: "campaign_membership",
                sql: "interview_status IS NULL OR interview_status IN ('NotStarted', 'InProgress', 'Completed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_membership_status",
                table: "campaign_membership",
                sql: "status IN ('Joined')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_source",
                table: "campaign_criteria",
                sql: "source IN ('AiSuggested', 'HrEdited')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs",
                sql: "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey')");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cv_submission_parse_status",
                table: "cv_submission");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cv_submission_status",
                table: "cv_submission");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_status",
                table: "campaigns");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_questions_source",
                table: "campaign_questions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_membership_interview_status",
                table: "campaign_membership");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_membership_status",
                table: "campaign_membership");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_source",
                table: "campaign_criteria");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_action",
                table: "audit_logs");

        }
    }
}
