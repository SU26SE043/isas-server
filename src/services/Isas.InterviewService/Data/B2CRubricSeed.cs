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
    // BE — Backend
    private static readonly Guid BeTechnical      = new("0be00000-0000-0000-0000-000000000001");
    private static readonly Guid BeSystemDesign   = new("0be00000-0000-0000-0000-000000000002");
    private static readonly Guid BeProblemSolving = new("0be00000-0000-0000-0000-000000000003");
    private static readonly Guid BeCommunication  = new("0be00000-0000-0000-0000-000000000004");
    // FE — Frontend
    private static readonly Guid FeTechnical      = new("0fe00000-0000-0000-0000-000000000001");
    private static readonly Guid FeUiUx           = new("0fe00000-0000-0000-0000-000000000002");
    private static readonly Guid FeProblemSolving = new("0fe00000-0000-0000-0000-000000000003");
    private static readonly Guid FeCommunication  = new("0fe00000-0000-0000-0000-000000000004");

    /// <summary>
    /// Tạo MỚI danh sách tiêu chí seed (GUID cố định, giá trị bất biến). Trả instance mới mỗi lần
    /// gọi để an toàn khi Add vào nhiều DbContext (test) và cho HasData.
    /// </summary>
    public static List<RubricCriterion> Build() =>
    [
        // ── BA — Business Analyst (Σweight = 1.0000) ────────────────────────────────────────
        Criterion(BaRequirement,    JobCategory.BA, "Phân tích yêu cầu",
            "Bóc tách, làm rõ và cấu trúc hoá yêu cầu nghiệp vụ; đặt câu hỏi đúng chỗ.", 0.3000m),
        Criterion(BaCommunication,  JobCategory.BA, "Giao tiếp & trình bày",
            "Diễn đạt rõ ràng, mạch lạc, đúng đối tượng người nghe.", 0.2500m),
        Criterion(BaDomain,         JobCategory.BA, "Hiểu nghiệp vụ & các bên liên quan",
            "Nắm bối cảnh domain, mục tiêu và ràng buộc của các stakeholder.", 0.2500m),
        Criterion(BaProblemSolving, JobCategory.BA, "Tư duy giải quyết vấn đề",
            "Lập luận có căn cứ, cân nhắc phương án và đánh đổi.", 0.2000m),

        // ── BE — Backend (Σweight = 1.0000) ─────────────────────────────────────────────────
        Criterion(BeTechnical,      JobCategory.BE, "Chiều sâu kỹ thuật",
            "Hiểu bản chất ngôn ngữ/framework, cơ chế hoạt động và trade-off kỹ thuật.", 0.3000m),
        Criterion(BeSystemDesign,   JobCategory.BE, "Thiết kế hệ thống & CSDL",
            "Mô hình hoá dữ liệu, kiến trúc, khả năng mở rộng và độ tin cậy.", 0.2500m),
        Criterion(BeProblemSolving, JobCategory.BE, "Giải quyết vấn đề & thuật toán",
            "Phân rã vấn đề, lựa chọn giải pháp hợp lý, cân nhắc độ phức tạp.", 0.2500m),
        Criterion(BeCommunication,  JobCategory.BE, "Giao tiếp & trình bày",
            "Giải thích giải pháp kỹ thuật rõ ràng, dễ theo dõi.", 0.2000m),

        // ── FE — Frontend (Σweight = 1.0000) ────────────────────────────────────────────────
        Criterion(FeTechnical,      JobCategory.FE, "Chiều sâu kỹ thuật",
            "Nắm vững HTML/CSS/JS, framework FE, state management và hiệu năng render.", 0.3000m),
        Criterion(FeProblemSolving, JobCategory.FE, "Giải quyết vấn đề",
            "Xử lý bài toán UI/logic, gỡ lỗi và cân nhắc giải pháp.", 0.2500m),
        Criterion(FeCommunication,  JobCategory.FE, "Giao tiếp & trình bày",
            "Diễn đạt ý tưởng và giải pháp rõ ràng, mạch lạc.", 0.2500m),
        Criterion(FeUiUx,           JobCategory.FE, "Ý thức UI/UX & accessibility",
            "Quan tâm trải nghiệm người dùng, khả năng tiếp cận và tính nhất quán giao diện.", 0.2000m),
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
