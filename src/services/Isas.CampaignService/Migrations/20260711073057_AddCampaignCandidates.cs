using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "keywords_any",
                table: "campaigns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_years_experience",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "required_skills",
                table: "campaigns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "campaign_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    full_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    cv_file_url = table.Column<string>(type: "text", nullable: true),
                    cv_parsed_text = table.Column<string>(type: "text", nullable: true),
                    parse_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reject_reason = table.Column<string>(type: "text", nullable: true),
                    skills = table.Column<string>(type: "jsonb", nullable: true),
                    years_experience = table.Column<decimal>(type: "numeric(4,1)", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    overall_match_score = table.Column<int>(type: "integer", nullable: true),
                    last_screening_published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_candidates", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_candidates_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "candidate_criterion_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_score = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    reasoning = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidate_criterion_scores", x => x.id);
                    table.ForeignKey(
                        name: "fk_candidate_criterion_scores_campaign_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "campaign_candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_candidate_criterion_scores_campaign_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "campaign_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_candidates_campaign_id_email",
                table: "campaign_candidates",
                columns: new[] { "campaign_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_candidates_campaign_id_status",
                table: "campaign_candidates",
                columns: new[] { "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_candidates_candidate_id",
                table: "campaign_candidates",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_candidate_criterion_scores_candidate_id_criterion_id",
                table: "candidate_criterion_scores",
                columns: new[] { "candidate_id", "criterion_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_candidate_criterion_scores_criterion_id",
                table: "candidate_criterion_scores",
                column: "criterion_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_criterion_scores");

            migrationBuilder.DropTable(
                name: "campaign_candidates");

            migrationBuilder.DropColumn(
                name: "keywords_any",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "min_years_experience",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "required_skills",
                table: "campaigns");
        }
    }
}
