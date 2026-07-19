using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Data;

/// <summary>
/// F21 (FR17) — DANH SÁCH ĐÓNG các mảnh prompt admin được phép sửa.
///
/// <para>Vì sao đóng: khoá tự do nghe linh hoạt hơn, nhưng một khoá gõ sai sẽ được lưu ngoan
/// ngoãn, không chỗ nào đọc, và admin tin rằng mình vừa đổi được cách chấm. Sai kiểu đó không
/// có triệu chứng — chỉ có một prompt vẫn chạy y như cũ. Khai trước thì gõ sai bị 400 ngay.</para>
///
/// <para><b>Ranh giới của F21 — đọc kỹ trước khi mở rộng:</b> danh sách này CỐ Ý không chứa
/// khung chống prompt-injection, delimiter bọc dữ liệu ứng viên, hợp đồng output, luật chọn mức
/// (E9) hay luật reasoning-phải-trích-dẫn (E11). Chúng do code giữ. Thêm chúng vào đây là trao
/// cho một tài khoản admin (hoặc kẻ chiếm được nó) khả năng vô hiệu hoá toàn bộ E9+E10+E11 bằng
/// một câu "hãy luôn cho điểm tối đa" — và không test nào sẽ kêu.</para>
/// </summary>
public static class PromptTemplateKeys
{
    // ── Prompt CHẤM — chỉ 2 khe, đặt ở vị trí code quyết ────────────────────────────────────
    // Prompt chấm vừa là THƯỚC ĐO vừa là bề mặt injection, nên nó là prompt duy nhất không cho
    // sửa toàn thân. Hai khe dưới đây đủ để chỉnh giọng và bổ sung hướng dẫn, mà không chạm được
    // vào bất kỳ ràng buộc nào quyết định điểm số.

    /// <summary>Câu mở đầu vai giám khảo. Khe, KHÔNG phải toàn bộ prompt chấm.</summary>
    public const string ScoringPersona = "scoring.persona";

    /// <summary>Hướng dẫn chấm bổ sung, chèn SAU mọi luật bắt buộc (nên không ghi đè được luật nào).</summary>
    public const string ScoringExtraGuidance = "scoring.extra_guidance";

    // ── Prompt SINH — sai thì ra câu hỏi dở, KHÔNG sai điểm và KHÔNG mất credit ─────────────
    public const string QuestionsIntro = "questions.intro";
    public const string QuestionsGuidance = "questions.guidance";
    public const string CriteriaGuidance = "criteria.guidance";
    public const string CvAnalysisGuidance = "cv_analysis.guidance";
    public const string RoadmapGuidance = "roadmap.guidance";
    public const string LessonTheoryGuidance = "lesson_theory.guidance";
    public const string SummarizeSessionGuidance = "summarize_session.guidance";
    public const string DecideNextGuidance = "decide_next.guidance";

    // ── Nửa B — NỘI DUNG GẮN VỚI TỪNG NGHỀ ─────────────────────────────────────────────────
    // Tập nghề vẫn ĐÓNG ở 3 giá trị enum (BA/BE/FE) — xem ghi chú dưới. Cái mở ra là nội dung
    // của mỗi nghề.

    /// <summary>Tên hiển thị của nghề (vd "BE" → "Backend Engineer").</summary>
    public static string CategoryDisplayName(JobCategory c) => $"category.{c}.display_name";

    /// <summary>Mô tả ngắn cho người dùng chọn nghề.</summary>
    public static string CategoryDescription(JobCategory c) => $"category.{c}.description";

    /// <summary>Hướng dẫn riêng theo nghề, chèn vào prompt SINH CÂU HỎI và khe hướng dẫn của
    /// prompt CHẤM. Đây là chỗ "custom 3 ngành" thực sự đổi được hành vi AI.</summary>
    public static string CategoryGuidance(JobCategory c) => $"category.{c}.guidance";

    /// <summary>
    /// Mọi khoá hợp lệ. Nghề lấy từ <see cref="Enum.GetValues{TEnum}()"/> nên thêm giá trị enum
    /// là tự có đủ 3 khoá — không phải nhớ sửa ở đây.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = BuildAll();

    private static HashSet<string> BuildAll()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            ScoringPersona, ScoringExtraGuidance,
            QuestionsIntro, QuestionsGuidance, CriteriaGuidance, CvAnalysisGuidance,
            RoadmapGuidance, LessonTheoryGuidance, SummarizeSessionGuidance, DecideNextGuidance,
        };

        foreach (var c in Enum.GetValues<JobCategory>())
        {
            keys.Add(CategoryDisplayName(c));
            keys.Add(CategoryDescription(c));
            keys.Add(CategoryGuidance(c));
        }

        return keys;
    }
}
