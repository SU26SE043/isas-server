using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignMaxAttemptsAtt1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_attempts",
                table: "campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_max_attempts_range",
                table: "campaigns",
                sql: "max_attempts BETWEEN 1 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_max_attempts_range",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "max_attempts",
                table: "campaigns");
        }
    }
}
