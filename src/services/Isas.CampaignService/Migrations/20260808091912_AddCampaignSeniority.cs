using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignSeniority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "seniority",
                table: "campaigns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Junior");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_seniority",
                table: "campaigns",
                sql: "seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_seniority",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "seniority",
                table: "campaigns");
        }
    }
}
