using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCriteriaAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "campaign_criteria",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_criteria", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_criteria_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_id_at",
                table: "audit_logs",
                columns: new[] { "entity_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_campaign_id",
                table: "campaign_criteria",
                column: "campaign_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "campaign_criteria");
        }
    }
}
