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
    public const string CvRequirementsWorkflow = "cv_requirements.workflow";
    public const string CvRequirementsLevelRubric = "cv_requirements.level_rubric";
    public const string JdRequirementsGuidance = "jd_requirements.guidance";
    public const string RoadmapGuidance = "roadmap.guidance";
    public const string LessonTheoryGuidance = "lesson_theory.guidance";
    public const string SummarizeSessionGuidance = "summarize_session.guidance";
    public const string DecideNextGuidance = "decide_next.guidance";

    /// <summary>
    /// E9b — hướng dẫn bổ sung cho prompt sinh MỐC ĐIỂM của tiêu chí campaign (B2B).
    /// Chèn ở CUỐI prompt, SAU mọi luật bắt buộc: luật "mỗi tiêu chí phải có mốc 0 và mốc
    /// maxScore" do code giữ, vì thang thiếu mốc 0 khiến bài TRỐNG snap về mốc thấp nhất còn lại
    /// (ứng viên không nói gì vẫn có điểm) mà không lỗi nào nổ.
    /// </summary>
    public const string CriterionLevelsGuidance = "criterion_levels.guidance";

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

    // ── J4 — CẤP ĐỘ ỨNG VIÊN (Fresher/Junior/Middle/Senior) ────────────────────────────────
    // `calibration_block` (sinh câu hỏi) thôi hard-code: mỗi mức có khoá RIÊNG cho dòng mô tả
    // của chính nó (admin sửa một mức không đụng 3 mức còn lại) + một khoá trọng tâm CHẤM riêng
    // cho lớp chấm điểm (J5). Nghề × mức có thêm khoá kiến thức chuyên sâu.

    /// <summary>Dòng mô tả (một mức) trong bảng hiệu chỉnh độ khó câu gốc — prompt SINH.</summary>
    public static string SeniorityProfile(Seniority s) => $"seniority.{s}.profile";

    /// <summary>Trọng tâm CHẤM riêng cho mức này, chèn vào đuôi prompt CHẤM (J5), mặc định RỖNG.</summary>
    public static string SeniorityScoringFocus(Seniority s) => $"seniority.{s}.scoring_focus";

    /// <summary>Kiến thức chuyên sâu theo (nghề × mức) — chèn thêm vào khối hiệu chỉnh khi
    /// admin đã soạn cho đúng cặp (nghề, mức) đang dùng; mặc định RỖNG.</summary>
    public static string CategorySeniorityKnowledge(JobCategory c, Seniority s)
        => $"category.{c}.seniority.{s}.knowledge";

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
            CvRequirementsWorkflow, CvRequirementsLevelRubric, JdRequirementsGuidance,
            RoadmapGuidance, LessonTheoryGuidance, SummarizeSessionGuidance, DecideNextGuidance,
            CriterionLevelsGuidance,
        };

        foreach (var c in Enum.GetValues<JobCategory>())
        {
            keys.Add(CategoryDisplayName(c));
            keys.Add(CategoryDescription(c));
            keys.Add(CategoryGuidance(c));
        }

        foreach (var s in Enum.GetValues<Seniority>())
        {
            keys.Add(SeniorityProfile(s));
            keys.Add(SeniorityScoringFocus(s));
        }

        foreach (var c in Enum.GetValues<JobCategory>())
            foreach (var s in Enum.GetValues<Seniority>())
                keys.Add(CategorySeniorityKnowledge(c, s));

        return keys;
    }
}
