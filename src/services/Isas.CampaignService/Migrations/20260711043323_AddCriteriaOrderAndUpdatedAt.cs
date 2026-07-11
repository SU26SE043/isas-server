using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCriteriaOrderAndUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_campaign_criteria_campaign_id",
                table: "campaign_criteria");

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "campaign_criteria",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "order_no",
                table: "campaign_criteria",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "campaign_criteria",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_campaign_id_name",
                table: "campaign_criteria",
                columns: new[] { "campaign_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_campaign_id_order_no",
                table: "campaign_criteria",
                columns: new[] { "campaign_id", "order_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_campaign_criteria_campaign_id_name",
                table: "campaign_criteria");

            migrationBuilder.DropIndex(
                name: "ix_campaign_criteria_campaign_id_order_no",
                table: "campaign_criteria");

            migrationBuilder.DropColumn(
                name: "order_no",
                table: "campaign_criteria");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "campaign_criteria");

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "campaign_criteria",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_campaign_id",
                table: "campaign_criteria",
                column: "campaign_id");
        }
    }
}
