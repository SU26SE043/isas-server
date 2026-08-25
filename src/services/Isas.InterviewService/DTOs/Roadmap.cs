namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

// BC12 (D20) — DTO roadmap ôn tập cá nhân hoá B2C.

// POST /roadmaps — cvId optional (parse sẵn ở Files). jobCategory/level bắt buộc (enum sai → 400).
// BC17 — candidate CHỌN nguồn nuôi roadmap thay vì tự gom MỌI buổi Scored:
//   • SessionIds     — buổi luyện đã Scored làm baseline; rỗng/null → roadmap CHUẨN theo level (không gom).
//   • CvAnalysisId   — 1 phân tích CV đã có (BC7) → CHỈ ngữ cảnh prompt (không gọi lại /analyze-cv, KHÔNG trừ credit).
//   • PriorRoadmapId — final_report của 1 roadmap đã hoàn thành (BC15) → CHỈ ngữ cảnh prompt.
//   • Focus          — mô tả tự do muốn AI tập trung vào đâu (≤ 2000 ký tự).
// CvAnalysis + prior-roadmap + focus KHÔNG vào baseline — chỉ là bối cảnh cho AI.
public record CreateRoadmapRequest(
    JobCategory JobCategory,
    RoadmapLevel Level,
    Guid? CvId,
    string? Name = null,                      // BE-6 — tên tự đặt; vắng → server sinh mặc định
    IReadOnlyList<Guid>? SessionIds = null,   // BC17 — buổi luyện Scored candidate chọn làm baseline
    Guid? CvAnalysisId = null,                // BC17 — cv_analyses (BC7)
    Guid? PriorRoadmapId = null,              // BC17 — roadmaps.final_report (BC15)
    string? Focus = null,                     // BC17 — free-text
    string? Language = null,
    string? Scope = null,                     // BE-4 — "Quick"/"Standard"; null → "Standard" (hành vi cũ)
    // Chế độ lộ trình: "LevelUp" (mặc định — tiến lên cấp mục tiêu, hành vi cũ) hoặc "Reinforce"
    // (ôn lại: giữ nguyên trình độ, bám điểm yếu đo được). null → "LevelUp"; chuỗi rỗng/giá trị
    // lạ → 400 (BK36 — KHÔNG âm thầm rơi về mặc định). Xem RoadmapService.ValidateMode.
    string? Mode = null,
    // Trình độ NGHỀ NGHIỆP HIỆN TẠI candidate tự khai ở wizard — "Fresher"/"Junior"/"Middle"/
    // "Senior". null → suy từ cv_analyses (hành vi cũ, xem CvAnalysisId); có gửi → THẮNG giá trị
    // suy từ CV (người dùng biết trình độ của mình rõ hơn một suy đoán từ CV, và ~2/5 bản phân
    // tích không suy ra được gì — xem Entities/CvAnalysis.cs). Chuỗi rỗng/giá trị lạ → 400 (BK36 —
    // KHÔNG âm thầm rơi về mặc định). Xem RoadmapService.ValidateCurrentLevel.
    string? CurrentLevel = null
);

// Điểm yếu gửi xuống AIService /generate-roadmap (khớp WeaknessScore: criterionName + percentage).
// MIS1-B4 — CriterionIds: MỌI id rubric_criteria từng đứng sau CriterionName này trong các buổi đã
// chọn (KHÔNG chỉ id của buổi mới nhất). rubric_criteria có Version + custom-per-candidate: đổi
// version hoặc rubric giữa các buổi sinh ra id MỚI cho "cùng một tên" ⇒ lấy 1 id sẽ âm thầm bỏ sót
// mistake của những buổi mang id khác. Nullable vì record cũ (RoadmapLessonService.cs dựng từ
// Baseline — Dictionary<string,decimal> không mang id) không có id để gắn. KHÔNG dùng để gửi AI
// (AiServiceRoadmapGenerator.cs chỉ project criterionName+percentage — id không rò ra ngoài).
public record RoadmapWeakness(
    string CriterionName,
    decimal Percentage,
    IReadOnlyCollection<Guid>? CriterionIds = null);

// MIS1-B4 — 1 mục "vì sao sai / sửa sao" AI sinh khi mở lesson (MIS1-B3), khớp mistake_key của
// RoadmapMistake/Milestone.MistakeRefs. Shape giống MistakeReviewItem bên AIService NHƯNG là type
// ĐỘC LẬP — không share, hai bên chỉ khớp nhau qua hợp đồng JSON.
public record LessonMistakeReviewItem(string MistakeId, string WhatWentWrong, string HowToFixIt);

// BE-5 — bằng chứng HÀNH VI cho 1 tiêu chí YẾU: Reasoning (E11, luôn trích NGUYÊN VĂN lời ứng
// viên) của 2-3 answer điểm THẤP NHẤT — đã tải + cắt trần sẵn (RoadmapEvidenceLoader). Khớp
// WeaknessScore ở chỗ criterionName là snapshot tên (không phải id — rubric có thể đổi version),
// nhưng mang thêm chẩn đoán CỤ THỂ thay vì chỉ con số %.
public record CriterionEvidence(string CriterionName, IReadOnlyList<string> Reasoning);

// Kết quả AI /generate-roadmap (sync) — chỉ cấu trúc (title/focusCriteria/lessons.title), không điểm.
public record RoadmapGenAiResult(IReadOnlyList<GeneratedMilestone> Milestones);
public record GeneratedMilestone(string Title, IReadOnlyList<string> FocusCriteria, IReadOnlyList<GeneratedLesson> Lessons);
public record GeneratedLesson(string Title);

// { criterionName, deltaPct } — set khi milestone Completed (BC15); BC12 luôn null.
public record MilestoneImprovementResponse(string CriterionName, decimal DeltaPct);

// F15 — kết quả AIService /generate-lesson-theory: markdown + tài liệu học (đã qua allowlist
// tên miền phía AIService). Resources rỗng KHÔNG phải lỗi.
// RAG grounding — CitedChunkIds: id chunk grounding mà AI THẬT SỰ cite (Contract 2). Rỗng khi không
// truyền grounding / AI không cite → lesson ungrounded.
public record LessonTheoryResult(
    string TheoryMarkdown,
    IReadOnlyList<Entities.LessonResource> Resources,
    IReadOnlyList<string>? CitedChunkIds = null);

// F15 — 1 tài liệu học gợi ý trả cho FE. `url` CÓ THỂ NULL vì có chủ đích: link do AI sinh chỉ
// được giữ khi tên miền thuộc allowlist (AIService app/resources.py). FE: có url → render link kèm
// nhãn "chưa kiểm chứng"; không url → chỉ hiện tên (người học tự tra).
public record LessonResourceResponse(
    string Title,
    string Type,          // Doc | Course | Book | Video | Article
    string? Publisher,
    string? Url
);

public record LessonResponse(
    Guid Id,
    int OrderNo,
    string Title,
    string? TheoryContent,   // null khi chưa mở (BC14); list bỏ luôn theoryContent.
    Guid? SessionId,
    string Status,
    IReadOnlyList<LessonResourceResponse> Resources,   // F15 — rỗng khi chưa mở lesson / AI không gợi ý được
    // RAG grounding — nguồn UY TÍN đã cite cho lý thuyết bài học ({chunkId, sourceUrl, sourceTitle}).
    // 3 trạng thái như QuestionResponse.Citations: null = roadmap cũ (chưa precompute); [] = precompute
    // chạy nhưng corpus không phủ → ungrounded; non-empty = grounded. Chỉ surface khi kèm theory.
    IReadOnlyList<Citation>? Citations = null,

    // Số lần ĐÃ làm bài này (0 = chưa bấm Bắt đầu lần nào). Đếm từ `roadmap_lesson_attempts`, nên
    // bài học xong từ trước khi có bảng đó vẫn ra 1 nhờ backfill của migration.
    int AttemptCount = 0,

    /// <summary>
    /// Có hiện nút "Làm lại" không — SERVER quyết, FE KHÔNG tự suy từ <c>status</c>.
    ///
    /// <para>Nay = "bài đã hoàn thành" (chủ sở hữu là hiển nhiên: chỉ chủ roadmap mới đọc được
    /// response này). ⚠ CỐ Ý KHÔNG bao gồm số dư ví: đọc số dư là một lời gọi HTTP sang
    /// PaymentService, mà đường đọc lesson/roadmap hiện KHÔNG phụ thuộc Payment — thêm vào là đặt
    /// Payment lên đường tới hạn của việc XEM lộ trình (Payment chết → trang lộ trình chết hoặc nói
    /// dối). Số dư còn đổi được giữa lúc đọc và lúc bấm, nên FE vẫn PHẢI xử lý 402 dù cờ này là gì;
    /// đường "Bắt đầu" hôm nay cũng không gác theo số dư, giữ nguyên như vậy cho đối xứng.</para>
    /// </summary>
    bool CanRetry = false
);

public record MilestoneResponse(
    Guid Id,
    int OrderNo,
    string Title,
    IReadOnlyList<string> FocusCriteria,
    string Status,
    IReadOnlyList<MilestoneImprovementResponse>? Improvement,
    IReadOnlyList<LessonResponse> Lessons
);

// BE-4 — provenance của roadmap: NGUỒN đã dùng để tạo (sessionIds/baseline, ghi xuống DB từ BC12
// nhưng trước đây không endpoint nào trả lại — cột chết ở tầng API dù có ở tầng lưu trữ) + SCOPE
// đã dùng lúc tạo. `Scope` chỉ có giá trị NGAY LÚC TẠO (không lưu DB) — đọc roadmap cũ → null,
// KHÔNG suy đoán (xem ghi chú tại `RoadmapService.Map`).
public record RoadmapResolvedFromResponse(
    IReadOnlyList<Guid> SessionIds,
    bool BaselineAvailable,
    string? Scope
);

public record RoadmapResponse(
    Guid Id,
    string Name,                                   // BE-6 — luôn có giá trị, kể cả hàng cũ (suy lúc đọc)
    string JobCategory,
    string Level,
    // Chế độ lộ trình ("LevelUp" | "Reinforce"). ĐỌC THẲNG cột đã lưu — khác `ResolvedFrom.Scope`
    // vốn chỉ có nghĩa lúc tạo. Đặt cạnh `Level` vì nó QUYẾT ĐỊNH cách đọc `Level`: "Junior" là
    // đích nhắm tới ở LevelUp, nhưng là mức đang đứng ở Reinforce — FE hiện hai thứ đó khác nhau.
    string Mode,
    string Language,
    Guid? CvId,
    string Status,
    IReadOnlyList<MilestoneResponse> Milestones,   // theo orderNo
    DateTime CreatedAt,
    DateTime? CompletedAt,
    RoadmapResolvedFromResponse ResolvedFrom       // BE-4 — additive, xem RoadmapResolvedFromResponse
);

/// <summary>
/// Một dòng trong `GET /roadmaps` (DANH SÁCH). KHÔNG có <c>milestones</c> — khác
/// <see cref="RoadmapResponse"/> của endpoint chi tiết `GET /roadmaps/{id}`, vốn giữ nguyên đủ cây.
///
/// Vì sao bỏ hẳn thay vì trả cây rỗng: list trước đây <c>Include(Milestones).ThenInclude(Lessons)</c>
/// nên payload nhân theo cây (mỗi roadmap × mỗi milestone × mỗi lesson) cho một màn hình chỉ vẽ
/// tiêu đề + ngày + trạng thái. Đã đối chiếu FE (`isas-frontend`): trang danh sách roadmap chỉ đọc
/// id/jobCategory/level/createdAt/status, còn <c>milestones</c> chỉ được đọc ở trang CHI TIẾT (gọi
/// endpoint khác) ⇒ bỏ khỏi list không hỏng gì. Trả <c>[]</c> thì sẽ là nói dối ("roadmap này không
/// có chặng nào"), nên chọn bỏ hẳn key.
///
/// Cần hiển thị "N chặng" trên thẻ danh sách về sau → thêm <c>MilestoneCount</c> project bằng
/// subquery scalar (<c>x.Milestones.Count</c>), KHÔNG quay lại Include cả cây.
/// </summary>
public record RoadmapSummaryResponse(
    Guid Id,
    string Name,                                   // BE-6 — BẮT BUỘC có ở list, không chỉ ở chi tiết:
                                                   // trang danh sách mới là nơi ba lộ trình cùng hiện
                                                   // "Roadmap" cạnh nhau, tức chỗ vấn đề lộ ra rõ nhất
    string JobCategory,
    string Level,
    // Cùng lý do như ở `RoadmapResponse`, và trang DANH SÁCH mới là nơi cần nhất: ba lộ trình
    // cùng nghề + cùng level nằm cạnh nhau thì "ôn tập" hay "tiến lên" là thứ DUY NHẤT phân biệt
    // được chúng (đúng lập luận đã dùng cho `Name` ở BE-6).
    string Mode,
    Guid? CvId,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    // Roadmap này có báo cáo tổng kết (BC15) hay chưa — cờ để picker "chọn lộ trình đã hoàn tất"
    // của wizard lọc ĐÚNG thứ `RoadmapService.CreateAsync` chấp nhận.
    //
    // 🔑 KHÔNG lọc bằng `Status == Completed` được: `CreateAsync` gác theo `FinalReport` RỖNG HAY
    // KHÔNG (RoadmapService.cs, `IsNullOrWhiteSpace(prior.FinalReport)` → 400), còn
    // `RoadmapLessonService.RetryLessonAsync` mở lại roadmap `Completed → Active` và XOÁ
    // `FinalReport` ⇒ tồn tại roadmap từng hoàn tất mà nay không có báo cáo. Lọc theo status là
    // mời một roadmap rồi để người dùng ăn 400 SAU KHI đã chờ 13–54s tạo roadmap.
    //
    // Cùng nguyên tắc với RoadmapSessionEligibility: picker phải mang ĐÚNG vị ngữ mà guard dùng.
    bool HasFinalReport = false
);

// BE-6 — PATCH /roadmaps/{id}: đổi tên lộ trình. Cho đổi ở MỌI trạng thái, kể cả Completed — tên là
// nhãn của người dùng, không phải dữ liệu bị đóng băng theo kết quả học.
public record RenameRoadmapRequest(string Name);
