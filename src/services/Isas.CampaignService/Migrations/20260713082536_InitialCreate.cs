using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
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
                name: "campaign_rankings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total_score = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_rankings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    domain = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    max_candidates = table.Column<int>(type: "integer", nullable: true),
                    time_limit_minutes = table.Column<int>(type: "integer", nullable: true),
                    anti_cheat_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    pass_score_pct = table.Column<int>(type: "integer", nullable: true),
                    jd_file_url = table.Column<string>(type: "text", nullable: true),
                    jd_text = table.Column<string>(type: "text", nullable: true),
                    criteria_file_url = table.Column<string>(type: "text", nullable: true),
                    criteria_text = table.Column<string>(type: "text", nullable: true),
                    required_skills = table.Column<string>(type: "jsonb", nullable: true),
                    keywords_any = table.Column<string>(type: "jsonb", nullable: true),
                    min_years_experience = table.Column<int>(type: "integer", nullable: true),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaigns", x => x.id);
                });

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
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    interview_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
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
                name: "campaign_criteria",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_no = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
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

            migrationBuilder.CreateTable(
                name: "campaign_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_invitations", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_invitations_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaign_questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_text = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    time_limit_seconds = table.Column<int>(type: "integer", nullable: true),
                    is_required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_questions", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_questions_campaigns_campaign_id",
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
                name: "ix_audit_logs_entity_id_at",
                table: "audit_logs",
                columns: new[] { "entity_id", "at" });

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
                name: "ix_campaign_criteria_campaign_id_name",
                table: "campaign_criteria",
                columns: new[] { "campaign_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_campaign_id_order_no",
                table: "campaign_criteria",
                columns: new[] { "campaign_id", "order_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_invitations_campaign_id",
                table: "campaign_invitations",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_invitations_token",
                table: "campaign_invitations",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_questions_campaign_id",
                table: "campaign_questions",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_rankings_campaign_id_total_score",
                table: "campaign_rankings",
                columns: new[] { "campaign_id", "total_score" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_rankings_session_id",
                table: "campaign_rankings",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_org_id_created_at",
                table: "campaigns",
                columns: new[] { "org_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_org_id_status",
                table: "campaigns",
                columns: new[] { "org_id", "status" });

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
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "campaign_invitations");

            migrationBuilder.DropTable(
                name: "campaign_questions");

            migrationBuilder.DropTable(
                name: "campaign_rankings");

            migrationBuilder.DropTable(
                name: "candidate_criterion_scores");

            migrationBuilder.DropTable(
                name: "campaign_candidates");

            migrationBuilder.DropTable(
                name: "campaign_criteria");

            migrationBuilder.DropTable(
                name: "campaigns");
        }
    }
}
