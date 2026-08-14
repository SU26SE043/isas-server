using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddJobFitScreening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bonus_signals",
                table: "cv_submission",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fit_summary",
                table: "cv_submission",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gaps",
                table: "cv_submission",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "screening_version",
                table: "cv_submission",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "strengths",
                table: "cv_submission",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_risk",
                table: "cv_submission",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verify_questions",
                table: "cv_submission",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "job_needs",
                table: "campaigns",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bonus_signals",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "fit_summary",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "gaps",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "screening_version",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "strengths",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "verification_risk",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "verify_questions",
                table: "cv_submission");

            migrationBuilder.DropColumn(
                name: "job_needs",
                table: "campaigns");
        }
    }
}
