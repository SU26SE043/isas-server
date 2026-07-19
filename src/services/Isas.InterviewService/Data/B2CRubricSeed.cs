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
    // BE — Backend
    private static readonly Guid BeTechnical      = new("0be00000-0000-0000-0000-000000000001");
    private static readonly Guid BeSystemDesign   = new("0be00000-0000-0000-0000-000000000002");
    private static readonly Guid BeProblemSolving = new("0be00000-0000-0000-0000-000000000003");
    private static readonly Guid BeCommunication  = new("0be00000-0000-0000-0000-000000000004");
    private static readonly Guid BeLanguage       = new("0be00000-0000-0000-0000-000000000005");  // F12
    private static readonly Guid BeTerminology    = new("0be00000-0000-0000-0000-000000000006");  // F12
    // FE — Frontend
    private static readonly Guid FeTechnical      = new("0fe00000-0000-0000-0000-000000000001");
    private static readonly Guid FeUiUx           = new("0fe00000-0000-0000-0000-000000000002");
    private static readonly Guid FeProblemSolving = new("0fe00000-0000-0000-0000-000000000003");
    private static readonly Guid FeCommunication  = new("0fe00000-0000-0000-0000-000000000004");
    private static readonly Guid FeLanguage       = new("0fe00000-0000-0000-0000-000000000005");  // F12
    private static readonly Guid FeTerminology    = new("0fe00000-0000-0000-0000-000000000006");  // F12

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

    /// <summary>
    /// Tạo MỚI danh sách tiêu chí seed (GUID cố định, giá trị bất biến). Trả instance mới mỗi lần
    /// gọi để an toàn khi Add vào nhiều DbContext (test) và cho HasData.
    /// </summary>
    public static List<RubricCriterion> Build() =>
    [
        // ── BA — Business Analyst (Σweight = 1.0000) ────────────────────────────────────────
        Criterion(BaRequirement,    JobCategory.BA, "Phân tích yêu cầu",
            "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.", 0.2500m),
        Criterion(BaCommunication,  JobCategory.BA, "Giao tiếp & trình bày",
            "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.", 0.2000m),
        Criterion(BaDomain,         JobCategory.BA, "Hiểu nghiệp vụ & các bên liên quan",
            "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.", 0.2000m),
        Criterion(BaProblemSolving, JobCategory.BA, "Tư duy giải quyết vấn đề",
            "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.", 0.1500m),
        Criterion(BaLanguage,       JobCategory.BA, LanguageName, LanguageDesc, 0.1000m),
        Criterion(BaTerminology,    JobCategory.BA, TerminologyName,
            TerminologyDesc("phân tích nghiệp vụ",
                "stakeholder, user story, acceptance criteria, use case, business rule, backlog"), 0.1000m),

        // ── BE — Backend (Σweight = 1.0000) ─────────────────────────────────────────────────
        Criterion(BeTechnical,      JobCategory.BE, "Chiều sâu kỹ thuật",
            "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.", 0.2500m),
        Criterion(BeSystemDesign,   JobCategory.BE, "Thiết kế hệ thống & CSDL",
            "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.", 0.2000m),
        Criterion(BeProblemSolving, JobCategory.BE, "Giải quyết vấn đề & thuật toán",
            "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", 0.2000m),
        Criterion(BeCommunication,  JobCategory.BE, "Giao tiếp & trình bày",
            "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.", 0.1500m),
        Criterion(BeLanguage,       JobCategory.BE, LanguageName, LanguageDesc, 0.1000m),
        Criterion(BeTerminology,    JobCategory.BE, TerminologyName,
            TerminologyDesc("backend",
                "transaction, index, deadlock, idempotent, cache, race condition, ACID"), 0.1000m),

        // ── FE — Frontend (Σweight = 1.0000) ────────────────────────────────────────────────
        Criterion(FeTechnical,      JobCategory.FE, "Chiều sâu kỹ thuật",
            "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.", 0.2500m),
        Criterion(FeProblemSolving, JobCategory.FE, "Giải quyết vấn đề",
            "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.", 0.2000m),
        Criterion(FeCommunication,  JobCategory.FE, "Giao tiếp & trình bày",
            "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.", 0.2000m),
        Criterion(FeUiUx,           JobCategory.FE, "Ý thức UI/UX & accessibility",
            "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.", 0.1500m),
        Criterion(FeLanguage,       JobCategory.FE, LanguageName, LanguageDesc, 0.1000m),
        Criterion(FeTerminology,    JobCategory.FE, TerminologyName,
            TerminologyDesc("frontend",
                "reflow/repaint, hydration, virtual DOM, debounce, bundle, lazy-load, accessibility"), 0.1000m),
    ];

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
            CampaignId = null,        // B2C: rubric theo JobCategory, không thuộc campaign
            Version = RubricVersion,
        };
}
