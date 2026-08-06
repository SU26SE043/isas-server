using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignSlotsAndCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_membership_interview_status",
                table: "campaign_membership");

            migrationBuilder.AddColumn<int>(
                name: "max_concurrent_interviews",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "interview_deadline_at",
                table: "campaign_membership",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "slot_id",
                table: "campaign_membership",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "slot_id",
                table: "campaign_invitations",
                type: "uuid",
                nullable: true);

            // Existing running memberships predate the materialized deadline. Preserve the campaign
            // expiry as the conservative fallback used by the capacity guard; null remains null.
            migrationBuilder.Sql("""
                UPDATE campaign_membership AS m
                SET interview_deadline_at = c.expires_at
                FROM campaigns AS c
                WHERE m.campaign_id = c.id
                  AND m.interview_status = 'InProgress';
                """);

            migrationBuilder.CreateTable(
                name: "campaign_slots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_slots", x => x.id);
                    table.CheckConstraint("ck_campaign_slots_capacity", "capacity > 0");
                    table.CheckConstraint("ck_campaign_slots_range", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "fk_campaign_slots_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_campaign_id",
                table: "campaign_membership",
                column: "campaign_id",
                filter: "interview_status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_membership_slot_id",
                table: "campaign_membership",
                column: "slot_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_membership_interview_status",
                table: "campaign_membership",
                sql: "interview_status IS NULL OR interview_status IN ('NotStarted', 'InProgress', 'Abandoned', 'Completed')");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_invitations_slot_id",
                table: "campaign_invitations",
                column: "slot_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_slots_campaign_id_starts_at",
                table: "campaign_slots",
                columns: new[] { "campaign_id", "starts_at" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_campaign_invitations_campaign_slots_slot_id",
                table: "campaign_invitations",
                column: "slot_id",
                principalTable: "campaign_slots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_campaign_membership_campaign_slots_slot_id",
                table: "campaign_membership",
                column: "slot_id",
                principalTable: "campaign_slots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_campaign_invitations_campaign_slots_slot_id",
                table: "campaign_invitations");

            migrationBuilder.DropForeignKey(
                name: "fk_campaign_membership_campaign_slots_slot_id",
                table: "campaign_membership");

            migrationBuilder.DropTable(
                name: "campaign_slots");

            migrationBuilder.DropIndex(
                name: "ix_campaign_membership_campaign_id",
                table: "campaign_membership");

            migrationBuilder.DropIndex(
                name: "ix_campaign_membership_slot_id",
                table: "campaign_membership");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_membership_interview_status",
                table: "campaign_membership");

            migrationBuilder.DropIndex(
                name: "ix_campaign_invitations_slot_id",
                table: "campaign_invitations");

            migrationBuilder.DropColumn(
                name: "max_concurrent_interviews",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "interview_deadline_at",
                table: "campaign_membership");

            migrationBuilder.DropColumn(
                name: "slot_id",
                table: "campaign_membership");

            migrationBuilder.DropColumn(
                name: "slot_id",
                table: "campaign_invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_membership_interview_status",
                table: "campaign_membership",
                sql: "interview_status IS NULL OR interview_status IN ('NotStarted', 'InProgress', 'Completed')");
        }
    }
}
