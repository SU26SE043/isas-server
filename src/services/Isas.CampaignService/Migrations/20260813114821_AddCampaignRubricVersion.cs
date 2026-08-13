using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignRubricVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rubric_version",
                table: "campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "rubric_version_updated_at",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rubric_version_updated_by",
                table: "campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaigns_rubric_version_positive",
                table: "campaigns",
                sql: "rubric_version >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaigns_rubric_version_positive",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "rubric_version",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "rubric_version_updated_at",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "rubric_version_updated_by",
                table: "campaigns");
        }
    }
}
