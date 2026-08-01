using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignMaxDeepPerQuestionInt17b : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_adaptive_caps_non_negative",
                table: "campaigns");

            migrationBuilder.AddColumn<int>(
                name: "max_deep_per_question",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_adaptive_caps_non_negative",
                table: "campaigns",
                sql: "(max_follow_ups IS NULL OR max_follow_ups >= 0) AND (max_questions IS NULL OR max_questions >= 0) AND (max_deep_per_question IS NULL OR max_deep_per_question >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_adaptive_caps_non_negative",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "max_deep_per_question",
                table: "campaigns");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_adaptive_caps_non_negative",
                table: "campaigns",
                sql: "(max_follow_ups IS NULL OR max_follow_ups >= 0) AND (max_questions IS NULL OR max_questions >= 0)");
        }
    }
}
