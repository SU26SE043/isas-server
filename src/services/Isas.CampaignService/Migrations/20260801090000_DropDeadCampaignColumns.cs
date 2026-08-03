using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations;

/// <summary>
/// Separates destructive schema work from AddTrackBDatabaseHardening.
///
/// Deploy preflight (read-only, must both be 0 before applying):
/// SELECT count(*) FROM campaign_questions WHERE time_limit_seconds IS NOT NULL;
/// SELECT count(*) FROM campaign_invitations WHERE used_at IS NOT NULL;
///
/// The column rename keeps data, but can break external BI/ad-hoc queries. Notify those consumers
/// before the migration window. Dropped-column data cannot be recovered by Down().
/// </summary>
[DbContext(typeof(CampaignDbContext))]
[Migration("20260801090000_DropDeadCampaignColumns")]
public partial class DropDeadCampaignColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_candidate_criterion_scores_cv_submission_candidate_id",
            table: "candidate_criterion_scores");

        migrationBuilder.DropColumn(name: "time_limit_seconds", table: "campaign_questions");
        migrationBuilder.DropColumn(name: "used_at", table: "campaign_invitations");

        migrationBuilder.RenameColumn(
            name: "candidate_id",
            table: "candidate_criterion_scores",
            newName: "cv_submission_id");
        migrationBuilder.RenameIndex(
            name: "ix_candidate_criterion_scores_candidate_id_criterion_id",
            table: "candidate_criterion_scores",
            newName: "ix_candidate_criterion_scores_cv_submission_id_criterion_id");

        migrationBuilder.AddForeignKey(
            name: "fk_candidate_criterion_scores_cv_submission_cv_submission_id",
            table: "candidate_criterion_scores",
            column: "cv_submission_id",
            principalTable: "cv_submission",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_candidate_criterion_scores_cv_submission_cv_submission_id",
            table: "candidate_criterion_scores");
        migrationBuilder.RenameColumn(
            name: "cv_submission_id",
            table: "candidate_criterion_scores",
            newName: "candidate_id");
        migrationBuilder.RenameIndex(
            name: "ix_candidate_criterion_scores_cv_submission_id_criterion_id",
            table: "candidate_criterion_scores",
            newName: "ix_candidate_criterion_scores_candidate_id_criterion_id");

        // Schema rollback recreates columns only; dropped values cannot be reconstructed.
        migrationBuilder.AddColumn<int>(name: "time_limit_seconds", table: "campaign_questions", type: "integer", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "used_at", table: "campaign_invitations", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddForeignKey(
            name: "fk_candidate_criterion_scores_cv_submission_candidate_id",
            table: "candidate_criterion_scores",
            column: "candidate_id",
            principalTable: "cv_submission",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }
}
