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
            migrationBuilder.DropForeignKey(
                name: "fk_candidate_criterion_scores_cv_submission_candidate_id",
                table: "candidate_criterion_scores");

            migrationBuilder.DropColumn(
                name: "time_limit_seconds",
                table: "campaign_questions");

            migrationBuilder.DropColumn(
                name: "used_at",
                table: "campaign_invitations");

            migrationBuilder.RenameColumn(
                name: "candidate_id",
                table: "candidate_criterion_scores",
                newName: "cv_submission_id");

            migrationBuilder.RenameIndex(
                name: "ix_candidate_criterion_scores_candidate_id_criterion_id",
                table: "candidate_criterion_scores",
                newName: "ix_candidate_criterion_scores_cv_submission_id_criterion_id");

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

            migrationBuilder.AddForeignKey(
                name: "fk_candidate_criterion_scores_cv_submission_cv_submission_id",
                table: "candidate_criterion_scores",
                column: "cv_submission_id",
                principalTable: "cv_submission",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_candidate_criterion_scores_cv_submission_cv_submission_id",
                table: "candidate_criterion_scores");

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

            migrationBuilder.RenameColumn(
                name: "cv_submission_id",
                table: "candidate_criterion_scores",
                newName: "candidate_id");

            migrationBuilder.RenameIndex(
                name: "ix_candidate_criterion_scores_cv_submission_id_criterion_id",
                table: "candidate_criterion_scores",
                newName: "ix_candidate_criterion_scores_candidate_id_criterion_id");

            migrationBuilder.AddColumn<int>(
                name: "time_limit_seconds",
                table: "campaign_questions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "used_at",
                table: "campaign_invitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_candidate_criterion_scores_cv_submission_candidate_id",
                table: "candidate_criterion_scores",
                column: "candidate_id",
                principalTable: "cv_submission",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
