using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeSessionEntitlementSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "cv_analysis_included",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "entitlement_source",
                table: "practice_sessions",
                type: "text",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<bool>(
                name: "grounding_enabled",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "repo_analysis_included",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "roadmap_enabled",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "self_consistency_n",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "tier_code",
                table: "practice_sessions",
                type: "text",
                nullable: false,
                defaultValue: "free");

            migrationBuilder.AddColumn<int>(
                name: "tier_rank",
                table: "practice_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cv_analysis_included",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "entitlement_source",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "grounding_enabled",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "repo_analysis_included",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "roadmap_enabled",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "self_consistency_n",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "tier_code",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "tier_rank",
                table: "practice_sessions");
        }
    }
}
