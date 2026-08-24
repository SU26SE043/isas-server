using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddCriterionScoringMethodMeasuredFluency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔴 ĐÃ GỠ TAY 30 lời gọi `UpdateData` mà EF scaffold ra với `columns: new string[0],
            // values: new object[0]` — chúng sinh ra `UPDATE rubric_criteria SET  WHERE id = ...`,
            // tức LỖI CÚ PHÁP Postgres, và vì cả script nằm trong MỘT transaction nên nó kéo đổ
            // toàn bộ migration. EF sinh chúng cho 30 row seed có `scoring_method` đúng bằng giá trị
            // mặc định của cột ('Ai') nên không cột nào cần SET, nhưng nó vẫn phát ra lời gọi rỗng.
            //
            // ⚠ KHÔNG có test .NET nào bắt được lỗi này: nền test dùng SQLite + `EnsureCreated`, vốn
            // BỎ QUA migration hoàn toàn ⇒ 1305 test vẫn xanh 100% trong khi migration vỡ trên bản
            // thật. Cùng lớp với `defaultValue: ""` cho cột jsonb (F15) và `Sql()` thiếu dấu `;`
            // (DB14). `MigrationScaffoldingGuardTests` nay chặn đúng dạng này cho mọi migration sau.
            //
            // Gỡ là AN TOÀN tuyệt đối: lời gọi rỗng không set gì cả, nên bỏ nó đi không mất dữ liệu
            // nào — cột đã có DEFAULT 'Ai' ở tầng DB lo cho toàn bộ row cũ.
            migrationBuilder.AddColumn<string>(
                name: "scoring_method",
                table: "rubric_criteria",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Ai");

            migrationBuilder.AddColumn<int>(
                name: "delivery_scoring_version",
                table: "answer_scores",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000005"),
                column: "description",
                value: "Dùng từ chính xác, câu đủ ý và đúng cấu trúc. Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, diễn đạt lủng củng khiến người nghe khó bám ý. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói). KHÔNG xét từ đệm hay ngập ngừng — đó là CÁCH NÓI, đã có tiêu chí riêng đo bằng số đo âm thanh.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000007"),
                columns: new[] { "description", "scoring_method" },
                values: new object[] { "Đo NHỊP NÓI từ chính bản ghi âm: tỉ lệ thời gian im lặng và số lần dừng đáng kể. Điểm cao: nói liền mạch, ít quãng lặng giữa câu. Điểm thấp: dừng lâu và dừng nhiều lần, phải dò tìm từ giữa chừng. Tiêu chí này do hệ thống TỰ TÍNH từ số đo âm thanh, không do AI đọc bản chép lời chấm, nên nó không phụ thuộc câu trả lời đúng/sai hay đủ/thiếu kiến thức. Không đo được (bản ghi quá ngắn hoặc thiếu số đo) thì tiêu chí bị LOẠI khỏi điểm, không tính 0.", "DeliveryMetrics" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000005"),
                column: "description",
                value: "Uses accurate word choice and complete, well-structured sentences. Do not assess spelling or punctuation because the transcript is produced from speech recognition. Do not assess fillers or hesitation either: that is delivery, and a separate criterion measures it from the audio.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000007"),
                columns: new[] { "description", "scoring_method" },
                values: new object[] { "Measures speaking rhythm directly from the recording: the proportion of silent time and the number of significant pauses. The system computes this from acoustic measurements rather than having AI judge the transcript, so it does not depend on whether the answer was correct or complete. When it cannot be measured (recording too short, or metrics missing) the criterion is excluded from the score instead of being scored zero.", "DeliveryMetrics" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000005"),
                column: "description",
                value: "Dùng từ chính xác, câu đủ ý và đúng cấu trúc. Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, diễn đạt lủng củng khiến người nghe khó bám ý. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói). KHÔNG xét từ đệm hay ngập ngừng — đó là CÁCH NÓI, đã có tiêu chí riêng đo bằng số đo âm thanh.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000007"),
                columns: new[] { "description", "scoring_method" },
                values: new object[] { "Đo NHỊP NÓI từ chính bản ghi âm: tỉ lệ thời gian im lặng và số lần dừng đáng kể. Điểm cao: nói liền mạch, ít quãng lặng giữa câu. Điểm thấp: dừng lâu và dừng nhiều lần, phải dò tìm từ giữa chừng. Tiêu chí này do hệ thống TỰ TÍNH từ số đo âm thanh, không do AI đọc bản chép lời chấm, nên nó không phụ thuộc câu trả lời đúng/sai hay đủ/thiếu kiến thức. Không đo được (bản ghi quá ngắn hoặc thiếu số đo) thì tiêu chí bị LOẠI khỏi điểm, không tính 0.", "DeliveryMetrics" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000005"),
                column: "description",
                value: "Uses accurate word choice and complete, well-structured sentences. Do not assess spelling or punctuation because the transcript is produced from speech recognition. Do not assess fillers or hesitation either: that is delivery, and a separate criterion measures it from the audio.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000007"),
                columns: new[] { "description", "scoring_method" },
                values: new object[] { "Measures speaking rhythm directly from the recording: the proportion of silent time and the number of significant pauses. The system computes this from acoustic measurements rather than having AI judge the transcript, so it does not depend on whether the answer was correct or complete. When it cannot be measured (recording too short, or metrics missing) the criterion is excluded from the score instead of being scored zero.", "DeliveryMetrics" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000005"),
                column: "description",
                value: "Dùng từ chính xác, câu đủ ý và đúng cấu trúc. Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, diễn đạt lủng củng khiến người nghe khó bám ý. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói). KHÔNG xét từ đệm hay ngập ngừng — đó là CÁCH NÓI, đã có tiêu chí riêng đo bằng số đo âm thanh.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000007"),
                columns: new[] { "description", "scoring_method" },
                values: new object[] { "Đo NHỊP NÓI từ chính bản ghi âm: tỉ lệ thời gian im lặng và số lần dừng đáng kể. Điểm cao: nói liền mạch, ít quãng lặng giữa câu. Điểm thấp: dừng lâu và dừng nhiều lần, phải dò tìm từ giữa chừng. Tiêu chí này do hệ thống TỰ TÍNH từ số đo âm thanh, không do AI đọc bản chép lời chấm, nên nó không phụ thuộc câu trả lời đúng/sai hay đủ/thiếu kiến thức. Không đo được (bản ghi quá ngắn hoặc thiếu số đo) thì tiêu chí bị LOẠI khỏi điểm, không tính 0.", "DeliveryMetrics" });

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000005"),
                column: "description",
                value: "Uses accurate word choice and complete, well-structured sentences. Do not assess spelling or punctuation because the transcript is produced from speech recognition. Do not assess fillers or hesitation either: that is delivery, and a separate criterion measures it from the audio.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000007"),
                columns: new[] { "description", "scoring_method" },
                values: new object[] { "Measures speaking rhythm directly from the recording: the proportion of silent time and the number of significant pauses. The system computes this from acoustic measurements rather than having AI judge the transcript, so it does not depend on whether the answer was correct or complete. When it cannot be measured (recording too short, or metrics missing) the criterion is excluded from the score instead of being scored zero.", "DeliveryMetrics" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_rubric_criteria_scoring_method",
                table: "rubric_criteria",
                sql: "scoring_method IN ('Ai', 'DeliveryMetrics')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rubric_criteria_scoring_method",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "scoring_method",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "delivery_scoring_version",
                table: "answer_scores");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000005"),
                column: "description",
                value: "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000007"),
                column: "description",
                value: "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức (đã có tiêu chí khác lo).");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000005"),
                column: "description",
                value: "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100011-0000-0000-0000-000000000007"),
                column: "description",
                value: "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000005"),
                column: "description",
                value: "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000007"),
                column: "description",
                value: "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức (đã có tiêu chí khác lo).");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000005"),
                column: "description",
                value: "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00011-0000-0000-0000-000000000007"),
                column: "description",
                value: "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000005"),
                column: "description",
                value: "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000007"),
                column: "description",
                value: "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức (đã có tiêu chí khác lo).");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000005"),
                column: "description",
                value: "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.");

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00011-0000-0000-0000-000000000007"),
                column: "description",
                value: "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.");
        }
    }
}
