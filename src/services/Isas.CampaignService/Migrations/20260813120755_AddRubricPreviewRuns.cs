using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddRubricPreviewRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rubric_preview_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: true),
                    question_text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    billed = table.Column<bool>(type: "boolean", nullable: false),
                    rubric_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    rubric_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rubric_version = table.Column<int>(type: "integer", nullable: false),
                    samples = table.Column<string>(type: "jsonb", nullable: true),
                    prompt_version = table.Column<int>(type: "integer", nullable: true),
                    length_parity_warning = table.Column<bool>(type: "boolean", nullable: false),
                    error_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rubric_preview_runs", x => x.id);
                    table.CheckConstraint("ck_rubric_preview_runs_status", "status IN ('Running', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "fk_rubric_preview_runs_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rubric_preview_runs_campaign_id_created_at",
                table: "rubric_preview_runs",
                columns: new[] { "campaign_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_rubric_preview_runs_running",
                table: "rubric_preview_runs",
                column: "campaign_id",
                unique: true,
                filter: "status = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rubric_preview_runs");
        }
    }
}
