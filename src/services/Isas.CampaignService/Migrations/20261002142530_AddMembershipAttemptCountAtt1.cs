using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipAttemptCountAtt1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "campaign_membership",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // ATT1 — backfill: membership đã có buổi đang chạy / đã chấm = đã dùng 1 lượt. Dòng Abandoned
            // để 0 = được thêm một lượt (nhân nhượng có chủ đích; prod 02/10 có 0 dòng Abandoned).
            // Postgres raw SQL — SQLite/EnsureCreated KHÔNG chạy migration nên test .NET không phủ câu này.
            migrationBuilder.Sql(
                "UPDATE campaign_membership SET attempt_count = 1 "
                + "WHERE session_id IS NOT NULL AND interview_status IN ('InProgress', 'Completed');");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_membership_attempt_count_non_negative",
                table: "campaign_membership",
                sql: "attempt_count >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_membership_attempt_count_non_negative",
                table: "campaign_membership");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "campaign_membership");
        }
    }
}
