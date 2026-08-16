using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionSampleAnswerAndPool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "questions_per_session",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "question_group",
                table: "campaign_questions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sample_answer",
                table: "campaign_questions",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_questions_per_session_positive",
                table: "campaigns",
                sql: "questions_per_session IS NULL OR questions_per_session >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_questions_per_session_positive",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "questions_per_session",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "question_group",
                table: "campaign_questions");

            migrationBuilder.DropColumn(
                name: "sample_answer",
                table: "campaign_questions");
        }
    }
}
