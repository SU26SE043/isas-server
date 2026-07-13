using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class SeedB2CRubric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "rubric_criteria",
                columns: new[] { "id", "campaign_id", "description", "is_active", "job_category", "max_score", "name", "version", "weight" },
                values: new object[,]
                {
                    { new Guid("0b100000-0000-0000-0000-000000000001"), null, "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.", true, "BA", 5, "Phân tích yêu cầu", 1, 0.3000m },
                    { new Guid("0b100000-0000-0000-0000-000000000002"), null, "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.", true, "BA", 5, "Giao tiếp & trình bày", 1, 0.2500m },
                    { new Guid("0b100000-0000-0000-0000-000000000003"), null, "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.", true, "BA", 5, "Hiểu nghiệp vụ & các bên liên quan", 1, 0.2500m },
                    { new Guid("0b100000-0000-0000-0000-000000000004"), null, "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.", true, "BA", 5, "Tư duy giải quyết vấn đề", 1, 0.2000m },
                    { new Guid("0be00000-0000-0000-0000-000000000001"), null, "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.", true, "BE", 5, "Chiều sâu kỹ thuật", 1, 0.3000m },
                    { new Guid("0be00000-0000-0000-0000-000000000002"), null, "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.", true, "BE", 5, "Thiết kế hệ thống & CSDL", 1, 0.2500m },
                    { new Guid("0be00000-0000-0000-0000-000000000003"), null, "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", true, "BE", 5, "Giải quyết vấn đề & thuật toán", 1, 0.2500m },
                    { new Guid("0be00000-0000-0000-0000-000000000004"), null, "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.", true, "BE", 5, "Giao tiếp & trình bày", 1, 0.2000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000001"), null, "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.", true, "FE", 5, "Chiều sâu kỹ thuật", 1, 0.3000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000002"), null, "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.", true, "FE", 5, "Ý thức UI/UX & accessibility", 1, 0.2000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000003"), null, "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.", true, "FE", 5, "Giải quyết vấn đề", 1, 0.2500m },
                    { new Guid("0fe00000-0000-0000-0000-000000000004"), null, "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.", true, "FE", 5, "Giao tiếp & trình bày", 1, 0.2500m }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000004"));
        }
    }
}
