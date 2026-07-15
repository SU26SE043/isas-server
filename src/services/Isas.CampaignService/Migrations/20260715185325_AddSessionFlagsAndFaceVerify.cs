using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionFlagsAndFaceVerify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "face_verify_enabled",
                table: "campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "reference_image_key",
                table: "campaign_candidates",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "session_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signal_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_flags", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_flags_campaign_id_session_id",
                table: "session_flags",
                columns: new[] { "campaign_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_session_flags_session_id",
                table: "session_flags",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_flags");

            migrationBuilder.DropColumn(
                name: "face_verify_enabled",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "reference_image_key",
                table: "campaign_candidates");
        }
    }
}
