using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddTieredPlanCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    audience = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    interview_funding = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    monthly_quota = table.Column<int>(type: "integer", nullable: true),
                    adaptive_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    adaptive_max_questions = table.Column<int>(type: "integer", nullable: true),
                    adaptive_max_followups = table.Column<int>(type: "integer", nullable: true),
                    grounding_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    self_consistency_n = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    cv_analysis_included = table.Column<bool>(type: "boolean", nullable: false),
                    repo_analysis_included = table.Column<bool>(type: "boolean", nullable: false),
                    roadmap_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    max_questions_cap = table.Column<int>(type: "integer", nullable: true),
                    max_active_campaigns = table.Column<int>(type: "integer", nullable: true),
                    max_candidates_cap = table.Column<int>(type: "integer", nullable: true),
                    postpaid_eligible = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    seat_count = table.Column<int>(type: "integer", nullable: true),
                    entitlements_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    entitlements_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                    table.CheckConstraint("ck_plans_adaptive_caps", "adaptive_enabled OR (adaptive_max_questions IS NULL AND adaptive_max_followups IS NULL)");
                    table.CheckConstraint("ck_plans_audience", "audience IN ('B2C', 'B2B')");
                    table.CheckConstraint("ck_plans_b2c_no_b2b", "audience = 'B2B' OR (max_active_campaigns IS NULL AND max_candidates_cap IS NULL AND postpaid_eligible = false AND seat_count IS NULL)");
                    table.CheckConstraint("ck_plans_funding", "interview_funding IN ('Credit', 'Metered', 'Unlimited')");
                    table.CheckConstraint("ck_plans_maxq", "max_questions_cap IS NULL OR max_questions_cap BETWEEN 0 AND 20");
                    table.CheckConstraint("ck_plans_metered", "interview_funding <> 'Metered' OR monthly_quota > 0");
                    table.CheckConstraint("ck_plans_scn", "self_consistency_n >= 1");
                });

            migrationBuilder.InsertData(
                table: "plans",
                columns: new[] { "id", "adaptive_enabled", "adaptive_max_followups", "adaptive_max_questions", "audience", "code", "created_at", "cv_analysis_included", "entitlements_json", "entitlements_version", "grounding_enabled", "interview_funding", "is_active", "max_active_campaigns", "max_candidates_cap", "max_questions_cap", "monthly_quota", "name", "rank", "repo_analysis_included", "roadmap_enabled", "seat_count", "self_consistency_n", "updated_at" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), false, null, null, "B2C", "free", new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc), false, "[]", 1, false, "Credit", true, null, null, null, null, "Free", 0, false, false, null, 1, new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("10000000-0000-0000-0000-000000000002"), true, 3, 10, "B2C", "plus", new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "[]", 1, true, "Metered", true, null, null, 10, 30, "Plus", 1, false, true, null, 1, new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("10000000-0000-0000-0000-000000000003"), true, 5, 20, "B2C", "pro", new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "[]", 1, true, "Metered", true, null, null, 20, 100, "Pro", 2, true, true, null, 3, new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("20000000-0000-0000-0000-000000000001"), false, null, null, "B2B", "starter", new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc), false, "[]", 1, false, "Credit", true, 1, 25, null, null, "Starter", 0, false, false, 1, 1, new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "plans",
                columns: new[] { "id", "adaptive_enabled", "adaptive_max_followups", "adaptive_max_questions", "audience", "code", "created_at", "cv_analysis_included", "entitlements_json", "entitlements_version", "grounding_enabled", "interview_funding", "is_active", "max_active_campaigns", "max_candidates_cap", "max_questions_cap", "monthly_quota", "name", "postpaid_eligible", "rank", "repo_analysis_included", "roadmap_enabled", "seat_count", "self_consistency_n", "updated_at" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000002"), true, null, null, "B2B", "business", new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc), false, "[]", 1, true, "Credit", true, 10, 200, null, null, "Business", true, 1, false, false, 10, 1, new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("20000000-0000-0000-0000-000000000003"), true, null, null, "B2B", "enterprise", new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc), false, "[]", 1, true, "Credit", true, null, null, null, null, "Enterprise", true, 2, false, false, null, 1, new DateTime(2026, 8, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "ix_plans_audience_active",
                table: "plans",
                columns: new[] { "audience", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_plans_audience_code",
                table: "plans",
                columns: new[] { "audience", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
