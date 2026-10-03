using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipAbandonReasonAc2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AC2 — thuần additive, nullable. KHÔNG CHECK, KHÔNG độ dài (Interview thêm lý do mới thì Campaign
            // không được nổ — tiền lệ reject_reason/transcript_engine). KHÔNG backfill: dòng cũ để null =
            // "không biết" (BK23); suy ngược từ interview_status = Abandoned sẽ là bịa lý do.
            migrationBuilder.AddColumn<string>(
                name: "abandon_reason",
                table: "campaign_membership",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "abandon_reason",
                table: "campaign_membership");
        }
    }
}
