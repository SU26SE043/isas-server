using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguageRubricCriteriaF12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.1500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.1500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.1500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.InsertData(
                table: "rubric_criteria",
                columns: new[] { "id", "campaign_id", "candidate_id", "description", "is_active", "job_category", "max_score", "name", "version", "weight" },
                values: new object[,]
                {
                    { new Guid("0b100000-0000-0000-0000-000000000005"), null, null, "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).", true, "BA", 5, "Ngữ pháp & dùng từ", 1, 0.1000m },
                    { new Guid("0b100000-0000-0000-0000-000000000006"), null, null, "Dùng ĐÚNG thuật ngữ chuyên ngành phân tích nghiệp vụ và giải thích được thuật ngữ mình dùng (vd stakeholder, user story, acceptance criteria, use case, business rule, backlog). Điểm cao: gọi đúng tên khái niệm, dùng đúng ngữ cảnh, giải thích được khi cần. Điểm thấp: gọi sai tên khái niệm, dùng thuật ngữ sai ngữ cảnh, hoặc nói thuật ngữ nhưng không giải thích được ý nghĩa — chỉ nói chung chung né thuật ngữ.", true, "BA", 5, "Thuật ngữ chuyên ngành", 1, 0.1000m },
                    { new Guid("0be00000-0000-0000-0000-000000000005"), null, null, "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).", true, "BE", 5, "Ngữ pháp & dùng từ", 1, 0.1000m },
                    { new Guid("0be00000-0000-0000-0000-000000000006"), null, null, "Dùng ĐÚNG thuật ngữ chuyên ngành backend và giải thích được thuật ngữ mình dùng (vd transaction, index, deadlock, idempotent, cache, race condition, ACID). Điểm cao: gọi đúng tên khái niệm, dùng đúng ngữ cảnh, giải thích được khi cần. Điểm thấp: gọi sai tên khái niệm, dùng thuật ngữ sai ngữ cảnh, hoặc nói thuật ngữ nhưng không giải thích được ý nghĩa — chỉ nói chung chung né thuật ngữ.", true, "BE", 5, "Thuật ngữ chuyên ngành", 1, 0.1000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000005"), null, null, "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).", true, "FE", 5, "Ngữ pháp & dùng từ", 1, 0.1000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000006"), null, null, "Dùng ĐÚNG thuật ngữ chuyên ngành frontend và giải thích được thuật ngữ mình dùng (vd reflow/repaint, hydration, virtual DOM, debounce, bundle, lazy-load, accessibility). Điểm cao: gọi đúng tên khái niệm, dùng đúng ngữ cảnh, giải thích được khi cần. Điểm thấp: gọi sai tên khái niệm, dùng thuật ngữ sai ngữ cảnh, hoặc nói thuật ngữ nhưng không giải thích được ý nghĩa — chỉ nói chung chung né thuật ngữ.", true, "FE", 5, "Thuật ngữ chuyên ngành", 1, 0.1000m }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000006"));

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.3000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.3000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.3000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.2000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.2500m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.2500m);
        }
    }
}
