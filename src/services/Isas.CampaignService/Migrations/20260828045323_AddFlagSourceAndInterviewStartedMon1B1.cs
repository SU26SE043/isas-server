using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddFlagSourceAndInterviewStartedMon1B1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "session_flags",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Client");

            migrationBuilder.AddColumn<DateTime>(
                name: "interview_started_at",
                table: "campaign_membership",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_flags_source",
                table: "session_flags",
                sql: "source IN ('Client', 'Server')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_flags_source",
                table: "session_flags");

            migrationBuilder.DropColumn(
                name: "source",
                table: "session_flags");

            migrationBuilder.DropColumn(
                name: "interview_started_at",
                table: "campaign_membership");
        }
    }
}
