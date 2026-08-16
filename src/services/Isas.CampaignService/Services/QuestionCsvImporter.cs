using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Validation;

namespace Isas.CampaignService.Services;

/// <summary>
/// Đọc file CSV câu hỏi HR soạn → danh sách <see cref="QuestionItem"/>.
///
/// <para><b>CHỈ ĐỌC, KHÔNG ghi DB.</b> HR xem trước kết quả trên màn hình rồi mới bấm Lưu, và lượt Lưu
/// đó đi qua đúng <c>UpdateCampaignQuestionsAsync</c> sẵn có. Ba lý do chọn thế thay vì ghi thẳng:
/// (1) không đẻ ra đường ghi thứ hai phải nhân đôi guard Draft + audit + merge F10;
/// (2) đường ghi mới cần một <c>AuditAction</c> mới ⇒ migration đụng CHECK <c>ck_audit_logs_action</c>,
///     hết thuần additive;
/// (3) file hỏng mã hoá trở thành vô hại — HR nhìn thấy chữ lỗi rồi bấm huỷ, thay vì DB ăn text hỏng.</para>
///
/// <para>Thuần, không DbContext ⇒ test được không cần SQLite, và nếu ai đó sau này lỡ tay thêm
/// <c>SaveChangesAsync</c> vào giữa luồng nhập thì nó lộ ra ngay ở chữ ký.</para>
/// </summary>
public static class QuestionCsvImporter
{
    public const string ColumnQuestionText = "question_text";
    public const string ColumnSampleAnswer = "sample_answer";
    public const string ColumnIsRequired = "is_required";
    public const string ColumnGroup = "nhom";

    private static readonly string[] AllColumns =
        [ColumnQuestionText, ColumnSampleAnswer, ColumnIsRequired, ColumnGroup];

    /// <summary>Giá trị được hiểu là "không bắt buộc". Mọi giá trị khác (kể cả trống) → bắt buộc.</summary>
    private static readonly HashSet<string> FalseWords =
        new(StringComparer.OrdinalIgnoreCase) { "false", "0", "no", "n", "không", "khong", "ko" };

    private static readonly HashSet<string> TrueWords =
        new(StringComparer.OrdinalIgnoreCase) { "true", "1", "yes", "y", "x", "có", "co" };

    /// <summary>
    /// Đọc nội dung file.
    /// </summary>
    /// <param name="bytes">Nội dung thô — decode ở đây chứ không nhận sẵn string, vì việc từ chối
    /// đúng lúc gặp byte không phải UTF-8 là một phần của hợp đồng (xem bên dưới).</param>
    /// <exception cref="ArgumentException">
    /// File hỏng ở mức KHÔNG đọc tiếp được (không phải UTF-8, thiếu cột bắt buộc, 0 dòng, quá số dòng).
    /// Lỗi của TỪNG DÒNG thì không ném — dòng đó vào <c>Errors</c>, các dòng khác vẫn trả về, đúng
    /// tiền lệ sàng CV hàng loạt ("hỏng = Rejected, không chặn cả batch").
    /// </exception>
    public static ImportQuestionsResult Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var text = DecodeStrictUtf8(bytes);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Excel ở locale VN xuất CSV bằng dấu CHẤM PHẨY. Thiếu dòng này thì cả file thành MỘT cột,
            // và triệu chứng lộ ra là "thiếu cột question_text" — chẩn đoán sai hẳn nguyên nhân.
            DetectDelimiter = true,
            TrimOptions = TrimOptions.Trim,
            PrepareHeaderForMatch = a => a.Header.Trim().ToLowerInvariant(),
            // Tự kiểm header để ra thông báo tiếng Việt có ích, thay vì exception mặc định của thư viện.
            HeaderValidated = null,
            MissingFieldFound = null,
            IgnoreBlankLines = true,
            BadDataFound = null,
        };

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
            throw new ArgumentException("File rỗng hoặc không đọc được dòng tiêu đề.");

        var header = (csv.HeaderRecord ?? [])
            .Select(h => h.Trim().ToLowerInvariant())
            .ToHashSet();

        if (!header.Contains(ColumnQuestionText))
            throw new ArgumentException(
                $"Thiếu cột '{ColumnQuestionText}'. File cần các cột: {string.Join(", ", AllColumns)}. "
                + "Bấm \"Tải file mẫu\" để lấy đúng định dạng.");

        var result = new ImportQuestionsResult();

        while (csv.Read())
        {
            // Dòng trong FILE: +1 vì header là dòng 1. HR mở Excel dò theo số này.
            var line = result.TotalRows + 2;

            var questionText = NormalizeCell(csv.TryGetField<string>(ColumnQuestionText, out var qt) ? qt : null);
            var sampleAnswer = NormalizeCell(csv.TryGetField<string>(ColumnSampleAnswer, out var sa) ? sa : null);
            var group = NormalizeCell(csv.TryGetField<string>(ColumnGroup, out var gr) ? gr : null);
            var isRequiredRaw = NormalizeCell(csv.TryGetField<string>(ColumnIsRequired, out var ir) ? ir : null);

            // Dòng trắng hoàn toàn (Excel hay để lại ở cuối file) — bỏ qua, không tính, không báo lỗi.
            if (questionText is null && sampleAnswer is null && group is null && isRequiredRaw is null)
                continue;

            result.TotalRows++;

            if (result.TotalRows > QuestionLimits.ImportMaxRows)
                throw new ArgumentException(
                    $"File có nhiều hơn {QuestionLimits.ImportMaxRows} dòng dữ liệu — vượt số câu hỏi "
                    + "tối đa của một chiến dịch.");

            if (questionText is null)
            {
                result.Errors.Add(new ImportRowError(line, ColumnQuestionText, "Thiếu nội dung câu hỏi."));
                continue;
            }

            if (questionText.Length > QuestionLimits.QuestionTextMaxChars)
            {
                result.Errors.Add(new ImportRowError(line, ColumnQuestionText,
                    $"Câu hỏi dài {questionText.Length} ký tự, tối đa {QuestionLimits.QuestionTextMaxChars}."));
                continue;
            }

            if (sampleAnswer is { Length: > QuestionLimits.SampleAnswerMaxChars })
            {
                result.Errors.Add(new ImportRowError(line, ColumnSampleAnswer,
                    $"Đáp án mẫu dài {sampleAnswer.Length} ký tự, tối đa {QuestionLimits.SampleAnswerMaxChars}."));
                continue;
            }

            if (group is { Length: > QuestionLimits.QuestionGroupMaxChars })
            {
                result.Errors.Add(new ImportRowError(line, ColumnGroup,
                    $"Tên nhóm dài {group.Length} ký tự, tối đa {QuestionLimits.QuestionGroupMaxChars}."));
                continue;
            }

            if (isRequiredRaw is not null
                && !TrueWords.Contains(isRequiredRaw) && !FalseWords.Contains(isRequiredRaw))
            {
                result.Errors.Add(new ImportRowError(line, ColumnIsRequired,
                    $"Giá trị '{isRequiredRaw}' không hiểu được. Dùng: có/không, true/false, 1/0, x, "
                    + "hoặc để trống (mặc định là bắt buộc)."));
                continue;
            }

            result.Questions.Add(new QuestionItem
            {
                // KHÔNG có Id: mọi dòng trong file là câu MỚI. Đây là điều làm kết quả nhập cắm thẳng
                // vào PUT /questions được mà không cần một tầng ánh xạ thứ hai (xem F10).
                QuestionText = questionText,
                SampleAnswer = sampleAnswer,
                QuestionGroup = group,
                // Trống → true, khớp default của cột `is_required`.
                IsRequired = isRequiredRaw is null || TrueWords.Contains(isRequiredRaw),
            });
        }

        if (result.TotalRows == 0)
            throw new ArgumentException("File không có dòng dữ liệu nào (chỉ có dòng tiêu đề).");

        return result;
    }

    /// <summary>File CSV mẫu cho HR tải về.</summary>
    public static byte[] BuildTemplate()
    {
        using var buffer = new MemoryStream();

        // BOM = true — CỐ Ý khác export kết quả (dùng UTF8Encoding(false)). Export là dữ liệu cho máy
        // đọc; file mẫu là thứ HR mở bằng Excel, và Excel không có BOM thì đọc CSV theo bảng mã hệ
        // thống ⇒ chính file DO TA PHÁT RA hiện tiếng Việt lỗi ngay khi vừa tải xuống.
        // Đừng "thống nhất" hai chỗ này thành một.
        using (var writer = new StreamWriter(buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var col in AllColumns) csv.WriteField(col);
            csv.NextRecord();

            // Dòng mẫu chọn để HR THẤY TẬN MẮT là dấu phẩy và xuống dòng không làm vỡ file — thay vì
            // đọc một dòng hướng dẫn rồi vẫn e ngại. CsvWriter tự bọc nháy đúng RFC4180.
            csv.WriteField("Giải thích index trong cơ sở dữ liệu dùng để làm gì?");
            csv.WriteField("Index giúp tìm nhanh, nhưng làm chậm INSERT, UPDATE và DELETE.");
            csv.WriteField("có");
            csv.WriteField("Cơ sở dữ liệu");
            csv.NextRecord();

            csv.WriteField("Kể về một lần bạn xử lý sự cố trên môi trường thật.");
            csv.WriteField("- Phát hiện: theo dõi cảnh báo\n- Xử lý: khoanh vùng rồi khôi phục\n- Sau đó: viết lại quy trình");
            csv.WriteField("không");
            csv.WriteField("Kinh nghiệm");
            csv.NextRecord();

            csv.WriteField("Bạn hiểu thế nào về cân bằng tải?");
            csv.WriteField("");
            csv.WriteField("");   // để trống = bắt buộc
            csv.WriteField("Thiết kế hệ thống");
            csv.NextRecord();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decode UTF-8 NGHIÊM: gặp byte không hợp lệ thì từ chối, KHÔNG đoán sang bảng mã khác.
    ///
    /// <para>Đoán sai với tiếng Việt gần như là tung đồng xu, và text sai sinh ra sẽ được LƯU VĨNH VIỄN;
    /// từ chối kèm hướng dẫn thì HR mất mười giây lưu lại file. Ném <see cref="ArgumentException"/>
    /// chứ không để <c>DecoderFallbackException</c> thoát ra — controller chỉ map ArgumentException
    /// sang 400, loại khác rơi xuống catch(Exception) thành 500.</para>
    /// </summary>
    private static string DecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            // Bỏ BOM nếu có: StreamReader mới tự bỏ, còn GetString thì không — để lại thì tên cột đầu
            // tiên thành "﻿question_text" và ta báo "thiếu cột" cho một file hoàn toàn đúng.
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return strict.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException(
                "File không phải mã hoá UTF-8 nên không đọc được tiếng Việt. Trong Excel chọn "
                + "File → Save As → \"CSV UTF-8 (Comma delimited)\" rồi tải lên lại.");
        }
    }

    /// <summary>Trim + bỏ ký tự điều khiển và ký tự vô hình; rỗng → null. Giữ xuống dòng và tab.</summary>
    private static string? NormalizeCell(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            // Ký tự vô hình hay lọt vào khi HR copy từ web/Word — không nhìn thấy nhưng làm lệch phép
            // so chuỗi (vd điều kiện "đáp án có đổi không" của R10) và tính vào độ dài.
            if (ch is '​' or '‌' or '‍' or '﻿') continue;
            if (char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t') continue;
            sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
