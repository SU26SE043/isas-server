using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Data;

/// <summary>
/// BC11 — Nguồn rubric B2C mặc định theo <see cref="JobCategory"/> (BA/BE/FE).
///
/// Đây là NGUỒN SỰ THẬT của bộ tiêu chí luyện tập cá nhân (campaign_id IS NULL). Không có bộ
/// tiêu chí này thì <c>AnswerService</c> thấy "không có tiêu chí active" → bỏ publish → answer
/// B2C không được chấm (xem interview.md §Đánh giá cách chấm tiêu chí #3).
///
/// Ràng buộc (INT-8/INT-10 · rules BC1):
///  - Mỗi nghề có ≥1 tiêu chí <c>IsActive</c>, <c>CampaignId == null</c>, cùng <c>Version</c>.
///  - Σ<c>Weight</c> mỗi nghề = 1.0000 (dùng cho điểm tổng có trọng số ở B2B/ranking; B2C lấy
///    trung bình cộng nên weight chỉ để hiển thị — vẫn giữ Σ=1 cho nhất quán dữ liệu).
///
/// IDEMPOTENT: <see cref="Build"/> luôn tạo lại các row với GUID CỐ ĐỊNH (hằng số bên dưới),
/// nên seed chạy lại KHÔNG nhân đôi — khoá chính trùng ⇒ migration/HasData không tạo row mới.
///
/// Cách giao seed: <c>HasData</c> ở <c>InterviewDbContext</c> (chỉ Npgsql) → EF sinh
/// <c>InsertData</c> literal trong migration → apply qua pipeline/tay (KHÔNG auto-migrate Neon,
/// KHÔNG seed lúc runtime).
/// </summary>
public static class B2CRubricSeed
{
    /// <summary>Version chung của bộ rubric B2C mặc định (1 nghề dùng chung 1 version).</summary>
    public const int RubricVersion = 1;

    /// <summary>Thang điểm tối đa mỗi tiêu chí (đồng nhất để dễ chuẩn hoá phần trăm).</summary>
    public const int DefaultMaxScore = 5;

    // ── GUID cố định (idempotency) — KHÔNG đổi sau khi đã seed lên môi trường ───────────────
    // BA — Business Analyst
    private static readonly Guid BaRequirement   = new("0b100000-0000-0000-0000-000000000001");
    private static readonly Guid BaCommunication = new("0b100000-0000-0000-0000-000000000002");
    private static readonly Guid BaDomain        = new("0b100000-0000-0000-0000-000000000003");
    private static readonly Guid BaProblemSolving= new("0b100000-0000-0000-0000-000000000004");
    private static readonly Guid BaLanguage      = new("0b100000-0000-0000-0000-000000000005");   // F12
    private static readonly Guid BaTerminology   = new("0b100000-0000-0000-0000-000000000006");   // F12
    private static readonly Guid BaFluency       = new("0b100000-0000-0000-0000-000000000007");   // F11
    // BE — Backend
    private static readonly Guid BeTechnical      = new("0be00000-0000-0000-0000-000000000001");
    private static readonly Guid BeSystemDesign   = new("0be00000-0000-0000-0000-000000000002");
    private static readonly Guid BeProblemSolving = new("0be00000-0000-0000-0000-000000000003");
    private static readonly Guid BeCommunication  = new("0be00000-0000-0000-0000-000000000004");
    private static readonly Guid BeLanguage       = new("0be00000-0000-0000-0000-000000000005");  // F12
    private static readonly Guid BeTerminology    = new("0be00000-0000-0000-0000-000000000006");  // F12
    private static readonly Guid BeFluency        = new("0be00000-0000-0000-0000-000000000007");  // F11
    // FE — Frontend
    private static readonly Guid FeTechnical      = new("0fe00000-0000-0000-0000-000000000001");
    private static readonly Guid FeUiUx           = new("0fe00000-0000-0000-0000-000000000002");
    private static readonly Guid FeProblemSolving = new("0fe00000-0000-0000-0000-000000000003");
    private static readonly Guid FeCommunication  = new("0fe00000-0000-0000-0000-000000000004");
    private static readonly Guid FeLanguage       = new("0fe00000-0000-0000-0000-000000000005");  // F12
    private static readonly Guid FeTerminology    = new("0fe00000-0000-0000-0000-000000000006");  // F12
    private static readonly Guid FeFluency        = new("0fe00000-0000-0000-0000-000000000007");  // F11

    // ── F12 (FR03) — mô tả 2 tiêu chí NGÔN NGỮ dùng chung 3 nghề ───────────────────────────
    //
    // Vì sao TÁCH khỏi "Giao tiếp & trình bày": tiêu chí đó chấm **mạch lạc nội dung** (ý có
    // thứ tự, đúng đối tượng nghe) — một người dùng từ sai, chia câu lủng củng vẫn có thể trình
    // bày mạch lạc, và ngược lại. Gộp lại thì FR03 không đo được.
    //
    // ⚠ TRANSCRIPT LÀ ASR (Whisper): chính tả/dấu câu/viết hoa là sản phẩm của bộ nhận dạng,
    // KHÔNG phải của ứng viên → mô tả dưới đây cố ý chỉ neo vào thứ SỐNG SÓT qua ASR (chọn từ,
    // cấu trúc câu, từ đệm, thuật ngữ) và prompt chấm có thêm 1 dòng cấm trừ điểm lỗi ASR
    // (xem prompts.py build_scoring_prompt).
    /// <summary>F12 — tên 2 tiêu chí ngôn ngữ (public để test/doc tham chiếu, không hard-code chuỗi).</summary>
    public const string LanguageName = "Ngữ pháp & dùng từ";

    /// <inheritdoc cref="LanguageName"/>
    public const string TerminologyName = "Thuật ngữ chuyên ngành";

    private const string LanguageDesc =
        "Dùng từ chính xác, câu đủ ý và đúng cấu trúc, ít từ đệm/lặp thừa (\"ờ\", \"kiểu như\"). " +
        "Điểm cao: câu gọn, chủ-vị rõ, chuyển ý mượt. Điểm thấp: câu cụt/dài lê thê không dứt ý, " +
        "dùng từ sai nghĩa, lặp từ đệm liên tục gây khó hiểu. " +
        "KHÔNG xét chính tả/dấu câu (transcript do máy chuyển từ giọng nói).";

    private static string TerminologyDesc(string role, string examples) =>
        $"Dùng ĐÚNG thuật ngữ chuyên ngành {role} và giải thích được thuật ngữ mình dùng " +
        $"(vd {examples}). Điểm cao: gọi đúng tên khái niệm, dùng đúng ngữ cảnh, giải thích được khi cần. " +
        "Điểm thấp: gọi sai tên khái niệm, dùng thuật ngữ sai ngữ cảnh, hoặc nói thuật ngữ " +
        "nhưng không giải thích được ý nghĩa — chỉ nói chung chung né thuật ngữ.";

    // ── F11 (FR06) — tiêu chí ĐỘ TRÔI CHẢY, dùng chung 3 nghề ──────────────────────────────
    //
    // Vì sao TÁCH khỏi "Ngữ pháp & dùng từ" (F12) và "Giao tiếp & trình bày": hai tiêu chí kia
    // chấm thứ đọc được TRONG CHỮ (chọn từ, cấu trúc câu, mạch lạc ý). Tiêu chí này chấm CÁCH NÓI
    // — nhịp nói, ngập ngừng, im lặng — thứ chỉ tồn tại trong ÂM THANH và trước F11 bị vứt sạch
    // trước khi tới bộ chấm. Gộp vào tiêu chí có sẵn thì FR06 vẫn không đo được gì.
    //
    // ⚠ Đây là tiêu chí DUY NHẤT được chấm dựa trên SỐ ĐO thật (tốc độ nói / khoảng lặng / từ đệm,
    // bơm vào prompt qua build_delivery_block). Prompt dặn hai điều; mô tả dưới đây cố ý KHÔNG
    // nhắc lại con số nào, để hai chỗ không lệch nhau khi tinh chỉnh ngưỡng:
    //   (1) số từ đệm đếm được là mức TỐI THIỂU (Whisper nuốt bớt) ⇒ "0 từ đệm" ≠ nói hoàn hảo;
    //   (2) chỉ số THỜI GIAN đáng tin hơn số đếm.
    /// <summary>F11 — tên tiêu chí độ trôi chảy (public để test/doc tham chiếu, không hard-code chuỗi).</summary>
    public const string FluencyName = "Độ trôi chảy & tự tin";

    private const string FluencyDesc =
        "Nói liền mạch, có nhịp, ít ngập ngừng — nghe ra sự tự tin. " +
        "Điểm cao: nhịp nói đều, dừng đúng chỗ ngắt ý, hiếm từ đệm, không phải dò tìm từ giữa câu. " +
        "Điểm thấp: dừng lâu giữa câu, nói nhát gừng, lặp lại đầu câu nhiều lần, " +
        "chèn dày từ đệm (\"ừm\", \"ờ\", \"kiểu như\") khiến người nghe khó bám ý. " +
        "CHỈ xét CÁCH NÓI — không xét câu trả lời đúng/sai hay đủ/thiếu kiến thức " +
        "(đã có tiêu chí khác lo).";

    /// <summary>
    /// Tạo MỚI danh sách tiêu chí seed (GUID cố định, giá trị bất biến). Trả instance mới mỗi lần
    /// gọi để an toàn khi Add vào nhiều DbContext (test) và cho HasData.
    /// </summary>
    public static List<RubricCriterion> Build()
    {
        var vietnamese = new List<RubricCriterion>
        {
        // ── BA — Business Analyst (Σweight = 1.0000) ────────────────────────────────────────
        Criterion(BaRequirement,    JobCategory.BA, "Phân tích yêu cầu",
            "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.", 0.2200m),
        Criterion(BaCommunication,  JobCategory.BA, "Giao tiếp & trình bày",
            "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.", 0.1800m),
        Criterion(BaDomain,         JobCategory.BA, "Hiểu nghiệp vụ & các bên liên quan",
            "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.", 0.1800m),
        Criterion(BaProblemSolving, JobCategory.BA, "Tư duy giải quyết vấn đề",
            "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.", 0.1400m),
        Criterion(BaLanguage,       JobCategory.BA, LanguageName, LanguageDesc, 0.0900m),
        Criterion(BaTerminology,    JobCategory.BA, TerminologyName,
            TerminologyDesc("phân tích nghiệp vụ",
                "stakeholder, user story, acceptance criteria, use case, business rule, backlog"), 0.0900m),
        Criterion(BaFluency,        JobCategory.BA, FluencyName, FluencyDesc, 0.1000m),

        // ── BE — Backend (Σweight = 1.0000) ─────────────────────────────────────────────────
        Criterion(BeTechnical,      JobCategory.BE, "Chiều sâu kỹ thuật",
            "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.", 0.2200m),
        Criterion(BeSystemDesign,   JobCategory.BE, "Thiết kế hệ thống & CSDL",
            "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.", 0.1800m),
        Criterion(BeProblemSolving, JobCategory.BE, "Giải quyết vấn đề & thuật toán",
            "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", 0.1800m),
        Criterion(BeCommunication,  JobCategory.BE, "Giao tiếp & trình bày",
            "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.", 0.1400m),
        Criterion(BeLanguage,       JobCategory.BE, LanguageName, LanguageDesc, 0.0900m),
        Criterion(BeTerminology,    JobCategory.BE, TerminologyName,
            TerminologyDesc("backend",
                "transaction, index, deadlock, idempotent, cache, race condition, ACID"), 0.0900m),
        Criterion(BeFluency,        JobCategory.BE, FluencyName, FluencyDesc, 0.1000m),

        // ── FE — Frontend (Σweight = 1.0000) ────────────────────────────────────────────────
        Criterion(FeTechnical,      JobCategory.FE, "Chiều sâu kỹ thuật",
            "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.", 0.2200m),
        Criterion(FeProblemSolving, JobCategory.FE, "Giải quyết vấn đề",
            "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.", 0.1800m),
        Criterion(FeCommunication,  JobCategory.FE, "Giao tiếp & trình bày",
            "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.", 0.1800m),
        Criterion(FeUiUx,           JobCategory.FE, "Ý thức UI/UX & accessibility",
            "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.", 0.1400m),
        Criterion(FeLanguage,       JobCategory.FE, LanguageName, LanguageDesc, 0.0900m),
        Criterion(FeTerminology,    JobCategory.FE, TerminologyName,
            TerminologyDesc("frontend",
                "reflow/repaint, hydration, virtual DOM, debounce, bundle, lazy-load, accessibility"), 0.0900m),
        Criterion(FeFluency,        JobCategory.FE, FluencyName, FluencyDesc, 0.1000m),
        };

        // English has an independent, deterministic id per Vietnamese seed. It keeps the exact
        // same weighting and category coverage, while preventing cross-language rubric mixing.
        var english = vietnamese.Select(v => new RubricCriterion
        {
            Id = EnglishId(v.Id),
            Name = EnglishName(v.Name),
            Description = EnglishDescription(v.Name, v.Description ?? string.Empty),
            Weight = v.Weight,
            MaxScore = v.MaxScore,
            IsActive = v.IsActive,
            JobCategory = v.JobCategory,
            Language = "en",
            CampaignId = null,
            Version = v.Version,
        });
        return vietnamese.Concat(english).ToList();
    }

    private static Guid EnglishId(Guid vietnameseId)
    {
        var bytes = vietnameseId.ToByteArray();
        bytes[0] ^= 0x11; // fixed and collision-free for the 21 seed ids above
        return new Guid(bytes);
    }

    private static string EnglishName(string name) => name switch
    {
        "Phân tích yêu cầu" => "Requirements analysis",
        "Giao tiếp & trình bày" => "Communication & presentation",
        "Hiểu nghiệp vụ & các bên liên quan" => "Business domain & stakeholders",
        "Tư duy giải quyết vấn đề" or "Giải quyết vấn đề" => "Problem solving",
        "Chiều sâu kỹ thuật" => "Technical depth",
        "Thiết kế hệ thống & CSDL" => "System design & databases",
        "Ý thức UI/UX & accessibility" => "UI/UX & accessibility awareness",
        "Ngữ pháp & dùng từ" => "Grammar & word choice",
        "Thuật ngữ chuyên ngành" => "Professional terminology",
        "Độ trôi chảy & tự tin" => "Fluency & confidence",
        _ => name,
    };

    private static string EnglishDescription(string name, string fallback) => name switch
    {
        "Ngữ pháp & dùng từ" => "Uses accurate word choice and complete, well-structured sentences with few fillers or unnecessary repetitions. Do not assess spelling or punctuation because the transcript is produced from speech recognition.",
        "Thuật ngữ chuyên ngành" => "Uses relevant professional terminology accurately and can explain terms in context. Assess the evidence in the spoken answer, not transcription spelling.",
        "Độ trôi chảy & tự tin" => "Speaks with a steady, confident rhythm and limited hesitation. Assess delivery only, not the correctness or completeness of technical content.",
        _ => fallback,
    };

    private static RubricCriterion Criterion(
        Guid id, JobCategory cat, string name, string description, decimal weight) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Weight = weight,
            MaxScore = DefaultMaxScore,
            IsActive = true,
            JobCategory = cat,
            Language = "vi",
            CampaignId = null,        // B2C: rubric theo JobCategory, không thuộc campaign
            Version = RubricVersion,
        };
}
