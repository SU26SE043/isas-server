using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.CampaignService.Migrations
{
    /// <inheritdoc />
    public partial class AddScoringPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cv_policy_version",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "interview_policy_version",
                table: "campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "scoring_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    engine_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    expression = table.Column<string>(type: "text", nullable: false),
                    pass_score_pct = table.Column<int>(type: "integer", nullable: true),
                    source_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scoring_policies", x => x.id);
                    table.CheckConstraint("ck_scoring_policies_kind", "kind IN ('Interview', 'CvScreening')");
                    table.CheckConstraint("ck_scoring_policies_pass_score_pct", "pass_score_pct IS NULL OR (pass_score_pct >= 0 AND pass_score_pct <= 100)");
                    table.CheckConstraint("ck_scoring_policies_version", "version >= 1");
                });

            migrationBuilder.InsertData(
                table: "scoring_policies",
                columns: new[] { "id", "campaign_id", "created_at", "created_by", "description", "engine_version", "expression", "kind", "name", "pass_score_pct", "source_template_id", "version" },
                values: new object[,]
                {
                    { new Guid("5c900001-0000-0000-0000-000000000000"), null, new DateTime(2026, 8, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, "Điểm tổng có trọng số của các tiêu chí — đúng công thức hệ thống đang dùng.", "1", "weighted_avg_pct", "Interview", "Như hiện nay", 60, null, 1 },
                    { new Guid("5c900002-0000-0000-0000-000000000000"), null, new DateTime(2026, 8, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, "Điểm tổng có trọng số nhân với tỷ lệ câu đã trả lời (0..1): bỏ càng nhiều câu điểm càng giảm.", "1", "weighted_avg_pct * completeness", "Interview", "Phạt bỏ câu", 60, null, 1 },
                    { new Guid("5c900003-0000-0000-0000-000000000000"), null, new DateTime(2026, 8, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, "Có tiêu chí nào dưới 40 thì lấy đúng điểm tiêu chí thấp nhất (không cho điểm mạnh bù điểm yếu); ngược lại lấy điểm tổng có trọng số.", "1", "if(min_pct < 40, min_pct, weighted_avg_pct)", "Interview", "Không bù trừ", 60, null, 1 },
                    { new Guid("5c900004-0000-0000-0000-000000000000"), null, new DateTime(2026, 8, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, "Tỷ lệ nhu cầu đạt: mỗi nhu cầu Strong tính 1, Partial tính 0.5, chia tổng số nhu cầu rồi nhân 100.", "1", "100 * (strong_count + 0.5 * partial_count) / need_count", "CvScreening", "Như hiện nay", 50, null, 1 },
                    { new Guid("5c900005-0000-0000-0000-000000000000"), null, new DateTime(2026, 8, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, "Thiếu bất kỳ nhu cầu must-have nào → 0 điểm; đủ must-have thì tính như 'Như hiện nay'.", "1", "if(must_have_met < must_have_total, 0, 100 * (strong_count + 0.5 * partial_count) / need_count)", "CvScreening", "Bắt buộc must-have", 50, null, 1 }
                });

            migrationBuilder.CreateIndex(
                name: "ux_scoring_policies_campaign",
                table: "scoring_policies",
                columns: new[] { "campaign_id", "kind", "version" },
                unique: true,
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_scoring_policies_template",
                table: "scoring_policies",
                columns: new[] { "kind", "name" },
                unique: true,
                filter: "campaign_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scoring_policies");

            migrationBuilder.DropColumn(
                name: "cv_policy_version",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "interview_policy_version",
                table: "campaigns");
        }
    }
}
