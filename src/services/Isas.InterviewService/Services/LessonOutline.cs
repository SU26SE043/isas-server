using System.Text;

namespace Isas.InterviewService.Services;

/// <summary>
/// Rút MỤC LỤC (các đề mục <c>##</c>) từ markdown bài giảng để đưa vào prompt sinh câu hỏi.
///
/// <para>Bài giảng do AIService sinh theo khuôn <c># Tiêu đề</c> rồi các khối <c>## Đề mục</c>
/// (xem <c>build_lesson_theory_prompt</c>). Chỉ lấy cấp <b>2</b>: cấp 1 là chính tiêu đề bài (đã
/// có trong <see cref="DTOs.LessonContext.Title"/>), cấp 3+ quá vụn cho việc khoanh chủ đề.</para>
///
/// <para><b>Trần là bắt buộc, không phải phòng xa.</b> Bài giảng dài nhất trên dev là 47.655 ký
/// tự; một bài bất thường (hoặc bài do prompt lỗi sinh ra toàn đề mục) sẽ đẩy prompt sinh câu hỏi
/// phình theo mà không ai thấy — chi phí token là thứ chỉ lộ ra ở hoá đơn cuối tháng.</para>
/// </summary>
public static class LessonOutline
{
    /// <summary>Số đề mục tối đa giữ lại. Trung bình thực đo 7,4 nên 12 chừa dư mà vẫn có trần.</summary>
    public const int MaxHeadings = 12;

    /// <summary>Độ dài tối đa một đề mục (ký tự) — cắt kèm dấu "…" để thấy được là đã cắt.</summary>
    public const int MaxHeadingLength = 120;

    /// <summary>
    /// Trả mục lục dạng nhiều dòng (mỗi dòng một đề mục, KHÔNG kèm dấu <c>#</c>), hoặc
    /// <c>null</c> khi không rút được đề mục nào.
    ///
    /// <para><c>null</c> chứ không phải chuỗi rỗng: bên nhận rẽ nhánh theo "có mục lục hay không",
    /// và chuỗi rỗng sẽ chèn một khối trống vào prompt.</para>
    /// </summary>
    public static string? From(string? theoryMarkdown)
    {
        if (string.IsNullOrWhiteSpace(theoryMarkdown)) return null;

        var sb = new StringBuilder();
        var kept = 0;

        foreach (var rawLine in theoryMarkdown.Split('\n'))
        {
            if (kept >= MaxHeadings) break;

            var line = rawLine.Trim();
            // Đúng cấp 2: "## " nhưng KHÔNG phải "### ". Kiểm cả dấu cách sau để không nuốt "##Foo"
            // (không phải heading markdown hợp lệ) lẫn cấp 3.
            if (!line.StartsWith("## ", StringComparison.Ordinal)) continue;

            var text = line[3..].Trim().TrimEnd('#').Trim();
            if (text.Length == 0) continue;
            if (text.Length > MaxHeadingLength)
                text = string.Concat(text.AsSpan(0, MaxHeadingLength), "…");

            if (kept > 0) sb.Append('\n');
            sb.Append(text);
            kept++;
        }

        return kept == 0 ? null : sb.ToString();
    }
}
