using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryMetricsAndFluencyCriterionF11 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "filler_breakdown",
                table: "practice_answers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "filler_count",
                table: "practice_answers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longest_pause_sec",
                table: "practice_answers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pause_count",
                table: "practice_answers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "silence_ratio",
                table: "practice_answers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "speech_rate_wpm",
                table: "practice_answers",
                type: "double precision",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.2200m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.1800m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.1800m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.1400m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000005"),
                column: "weight",
                value: 0.0900m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000006"),
                column: "weight",
                value: 0.0900m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.2200m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.1800m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.1800m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.1400m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000005"),
                column: "weight",
                value: 0.0900m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000006"),
                column: "weight",
                value: 0.0900m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000001"),
                column: "weight",
                value: 0.2200m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000002"),
                column: "weight",
                value: 0.1400m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000003"),
                column: "weight",
                value: 0.1800m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000004"),
                column: "weight",
                value: 0.1800m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000005"),
                column: "weight",
                value: 0.0900m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000006"),
                column: "weight",
                value: 0.0900m);

            migrationBuilder.InsertData(
                table: "rubric_criteria",
                columns: new[] { "id", "campaign_id", "candidate_id", "description", "is_active", "job_category", "max_score", "name", "version", "weight" },
                values: new object[,]
                {
                    { new Guid("0b100000-0000-0000-0000-000000000007"), null, null, "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức (đã có tiêu chí khác lo).", true, "BA", 5, "Độ trôi chảy & tự tin", 1, 0.1000m },
                    { new Guid("0be00000-0000-0000-0000-000000000007"), null, null, "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức (đã có tiêu chí khác lo).", true, "BE", 5, "Độ trôi chảy & tự tin", 1, 0.1000m },
                    { new Guid("0fe00000-0000-0000-0000-000000000007"), null, null, "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức (đã có tiêu chí khác lo).", true, "FE", 5, "Độ trôi chảy & tự tin", 1, 0.1000m }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000007"));

            migrationBuilder.DropColumn(
                name: "filler_breakdown",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "filler_count",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "longest_pause_sec",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "pause_count",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "silence_ratio",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "speech_rate_wpm",
                table: "practice_answers");

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
                keyValue: new Guid("0b100000-0000-0000-0000-000000000005"),
                column: "weight",
                value: 0.1000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000006"),
                column: "weight",
                value: 0.1000m);

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
                keyValue: new Guid("0be00000-0000-0000-0000-000000000005"),
                column: "weight",
                value: 0.1000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000006"),
                column: "weight",
                value: 0.1000m);

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

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000005"),
                column: "weight",
                value: 0.1000m);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000006"),
                column: "weight",
                value: 0.1000m);
        }
    }
}
