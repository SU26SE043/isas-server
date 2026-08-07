using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddBilingualLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_is_active",
                table: "rubric_criteria");

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "rubric_criteria",
                type: "text",
                nullable: false,
                defaultValue: "vi");

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "practice_sessions",
                type: "text",
                nullable: false,
                defaultValue: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000001"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000002"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000003"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000004"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000005"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000006"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000007"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000001"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000002"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000003"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000004"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000005"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000006"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000007"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000001"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000002"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000003"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000004"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000005"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000006"),
                column: "language",
                value: "vi");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000007"),
                column: "language",
                value: "vi");

            migrationBuilder.InsertData(
                table: "rubric_criteria",
                columns: new[] { "id", "campaign_id", "candidate_id", "description", "is_active", "job_category", "language", "max_score", "name", "version", "weight" },
                values: new object[,]
                {
                    { new Guid("0b100011-0000-0000-0000-000000000001"), null, null, "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.", true, "BA", "en", 5, "Requirements analysis", 1, 0.2200m },
                    { new Guid("0b100011-0000-0000-0000-000000000002"), null, null, "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.", true, "BA", "en", 5, "Communication & presentation", 1, 0.1800m },
                    { new Guid("0b100011-0000-0000-0000-000000000003"), null, null, "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.", true, "BA", "en", 5, "Business domain & stakeholders", 1, 0.1800m },
                    { new Guid("0b100011-0000-0000-0000-000000000004"), null, null, "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.", true, "BA", "en", 5, "Problem solving", 1, 0.1400m },
                    { new Guid("0b100011-0000-0000-0000-000000000005"), null, null, "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.", true, "BA", "en", 5, "Grammar & word choice", 1, 0.0900m },
                    { new Guid("0b100011-0000-0000-0000-000000000006"), null, null, "Uses relevant professional terminology accurately and can explain terms in context. Assess the evidence in the spoken answer, not transcription spelling.", true, "BA", "en", 5, "Professional terminology", 1, 0.0900m },
                    { new Guid("0b100011-0000-0000-0000-000000000007"), null, null, "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.", true, "BA", "en", 5, "Fluency & confidence", 1, 0.1000m },
                    { new Guid("0be00011-0000-0000-0000-000000000001"), null, null, "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.", true, "BE", "en", 5, "Technical depth", 1, 0.2200m },
                    { new Guid("0be00011-0000-0000-0000-000000000002"), null, null, "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.", true, "BE", "en", 5, "System design & databases", 1, 0.1800m },
                    { new Guid("0be00011-0000-0000-0000-000000000003"), null, null, "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", true, "BE", "en", 5, "Giải quyết vấn đề & thuật toán", 1, 0.1800m },
                    { new Guid("0be00011-0000-0000-0000-000000000004"), null, null, "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.", true, "BE", "en", 5, "Communication & presentation", 1, 0.1400m },
                    { new Guid("0be00011-0000-0000-0000-000000000005"), null, null, "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.", true, "BE", "en", 5, "Grammar & word choice", 1, 0.0900m },
                    { new Guid("0be00011-0000-0000-0000-000000000006"), null, null, "Uses relevant professional terminology accurately and can explain terms in context. Assess the evidence in the spoken answer, not transcription spelling.", true, "BE", "en", 5, "Professional terminology", 1, 0.0900m },
                    { new Guid("0be00011-0000-0000-0000-000000000007"), null, null, "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.", true, "BE", "en", 5, "Fluency & confidence", 1, 0.1000m },
                    { new Guid("0fe00011-0000-0000-0000-000000000001"), null, null, "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.", true, "FE", "en", 5, "Technical depth", 1, 0.2200m },
                    { new Guid("0fe00011-0000-0000-0000-000000000002"), null, null, "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.", true, "FE", "en", 5, "UI/UX & accessibility awareness", 1, 0.1400m },
                    { new Guid("0fe00011-0000-0000-0000-000000000003"), null, null, "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.", true, "FE", "en", 5, "Problem solving", 1, 0.1800m },
                    { new Guid("0fe00011-0000-0000-0000-000000000004"), null, null, "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.", true, "FE", "en", 5, "Communication & presentation", 1, 0.1800m },
                    { new Guid("0fe00011-0000-0000-0000-000000000005"), null, null, "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.", true, "FE", "en", 5, "Grammar & word choice", 1, 0.0900m },
                    { new Guid("0fe00011-0000-0000-0000-000000000006"), null, null, "Uses relevant professional terminology accurately and can explain terms in context. Assess the evidence in the spoken answer, not transcription spelling.", true, "FE", "en", 5, "Professional terminology", 1, 0.0900m },
                    { new Guid("0fe00011-0000-0000-0000-000000000007"), null, null, "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.", true, "FE", "en", 5, "Fluency & confidence", 1, 0.1000m }
                });

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_language_is_active",
                table: "rubric_criteria",
                columns: new[] { "candidate_id", "job_category", "language", "is_active" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_rubric_criteria_language",
                table: "rubric_criteria",
                sql: "language IN ('vi', 'en')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_sessions_language",
                table: "practice_sessions",
                sql: "language IN ('vi', 'en')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_language_is_active",
                table: "rubric_criteria");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rubric_criteria_language",
                table: "rubric_criteria");

            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_sessions_language",
                table: "practice_sessions");

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000007"));

            migrationBuilder.DropColumn(
                name: "language",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "language",
                table: "practice_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_is_active",
                table: "rubric_criteria",
                columns: new[] { "candidate_id", "job_category", "is_active" });
        }
    }
}
