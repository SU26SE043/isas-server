using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class SyncEnglishRubricDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000001"),
                column: "description",
                value: "Elicits, clarifies, and structures business requirements into testable, actionable outcomes.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000002"),
                column: "description",
                value: "Explains analysis and recommendations clearly, logically, and for the intended stakeholder audience.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000003"),
                column: "description",
                value: "Understands the business domain, objectives, constraints, and the perspectives of relevant stakeholders.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000004"),
                column: "description",
                value: "Builds evidence-based reasoning, compares viable options, and communicates trade-offs.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000001"),
                column: "description",
                value: "Demonstrates sound understanding of languages, frameworks, runtime behavior, and technical trade-offs.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000002"),
                column: "description",
                value: "Designs data models and system architecture with scalability, reliability, and consistency in mind.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000003"),
                columns: new[] { "description", "name" },
                values: new object[] { "Breaks down problems, chooses appropriate algorithms, and considers complexity and edge cases.", "Problem solving" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000004"),
                column: "description",
                value: "Explains technical solutions clearly enough for others to follow the reasoning and implementation choices.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000001"),
                column: "description",
                value: "Demonstrates practical knowledge of HTML, CSS, JavaScript, frontend frameworks, state management, and rendering performance.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000002"),
                column: "description",
                value: "Considers user experience, accessibility, and consistent interface behavior in proposed solutions.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000003"),
                column: "description",
                value: "Solves UI and application-logic problems methodically, including debugging and trade-off analysis.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000004"),
                column: "description",
                value: "Communicates product and technical ideas clearly, coherently, and with an appropriate level of detail.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000001"),
                column: "description",
                value: "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000002"),
                column: "description",
                value: "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000003"),
                column: "description",
                value: "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000004"),
                column: "description",
                value: "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000001"),
                column: "description",
                value: "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000002"),
                column: "description",
                value: "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000003"),
                columns: new[] { "description", "name" },
                values: new object[] { "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", "Giải quyết vấn đề & thuật toán" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000004"),
                column: "description",
                value: "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000001"),
                column: "description",
                value: "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000002"),
                column: "description",
                value: "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000003"),
                column: "description",
                value: "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000004"),
                column: "description",
                value: "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.");
        }
    }
}
