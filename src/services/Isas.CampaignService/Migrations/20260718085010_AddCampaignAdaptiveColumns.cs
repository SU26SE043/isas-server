using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignAdaptiveColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "adaptive_enabled",
                table: "campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "max_follow_ups",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_questions",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_adaptive_caps_non_negative",
                table: "campaigns",
                sql: "(max_follow_ups IS NULL OR max_follow_ups >= 0) AND (max_questions IS NULL OR max_questions >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_adaptive_caps_non_negative",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "adaptive_enabled",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "max_follow_ups",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "max_questions",
                table: "campaigns");
        }
    }
}
