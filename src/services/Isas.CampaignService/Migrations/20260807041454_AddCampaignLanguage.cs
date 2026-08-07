using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "campaigns",
                type: "text",
                nullable: false,
                defaultValue: "vi");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_language",
                table: "campaigns",
                sql: "language IN ('vi', 'en')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_language",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "language",
                table: "campaigns");
        }
    }
}
