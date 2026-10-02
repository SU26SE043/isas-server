using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddB2CScoreFormulaAndRubricBaseVersionRub1 : Migration
    {
        // RUB1 — hai cột NULLABLE, KHÔNG default, KHÔNG backfill (thuần additive):
        //   • practice_sessions.b2c_score_formula: công thức gộp điểm B2C ghim lúc tạo buổi. null = buổi
        //     trước RUB1 ⇒ trung bình cộng (không hồi tố). CHECK chặn giá trị lạ ở tầng DB.
        //   • rubric_criteria.based_on_default_version: bộ chuẩn version mấy lúc rubric riêng được lưu.
        //     null = không biết (rubric riêng có trước cột này) / không áp dụng (bộ chuẩn, campaign).
        //
        // (Scaffolder sinh kèm 42 lệnh `UpdateData(... based_on_default_version = null)` cho seed HasData
        // — thuần no-op trên cột vừa thêm, đã gỡ theo tiền lệ AddRubricSourceCriterionIdRnk1.)

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "based_on_default_version",
                table: "rubric_criteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "b2c_score_formula",
                table: "practice_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_sessions_b2c_score_formula",
                table: "practice_sessions",
                sql: "b2c_score_formula IS NULL OR b2c_score_formula IN ('Average', 'Weighted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_sessions_b2c_score_formula",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "based_on_default_version",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "b2c_score_formula",
                table: "practice_sessions");
        }
    }
}
