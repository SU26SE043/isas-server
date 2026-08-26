namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

// BC12 (D20) — DTO roadmap ôn tập cá nhân hoá B2C.

// POST /roadmaps — cvId optional (parse sẵn ở Files). jobCategory bắt buộc (enum sai → 400).
// BC17 — candidate CHỌN nguồn nuôi roadmap thay vì tự gom MỌI buổi Scored:
//   • SessionIds     — buổi luyện đã Scored làm baseline; rỗng/null → roadmap CHUẨN theo level (không gom).
//   • CvAnalysisId   — 1 phân tích CV đã có (BC7) → CHỈ ngữ cảnh prompt (không gọi lại /analyze-cv, KHÔNG trừ credit).
//   • PriorRoadmapId — final_report của 1 roadmap đã hoàn thành (BC15) → CHỈ ngữ cảnh prompt.
//   • Focus          — mô tả tự do muốn AI tập trung vào đâu (≤ 2000 ký tự).
// CvAnalysis + prior-roadmap + focus KHÔNG vào baseline — chỉ là bối cảnh cho AI.
public record CreateRoadmapRequest(
    JobCategory JobCategory,
    // REC1-B2 — NULLABLE, GIỮ NGUYÊN vị trí 2, KHÔNG thêm default: một tham số positional không
    // default bị ASP.NET coi là BẮT BUỘC — client bỏ gửi `level` sẽ ăn 400 (repo đã dính đúng lỗi
    // này, N7). Service KHÔNG CÒN ĐỌC giá trị này — mức lộ trình nay SUY từ chính buổi nguồn (mức
    // CAO NHẤT trong các buổi đã chọn), không phải lời tự khai chưa ai hiệu chuẩn (đo trên
    // production: chỉ 4/61 buổi đạt ngưỡng cấp của chính mình). Field vẫn khai để 59+ call site
    // positional cũ (gửi `RoadmapLevel` thật) không vỡ biên dịch — `RoadmapLevel` → `RoadmapLevel?`
    // là chuyển đổi ngầm định hợp lệ.
    RoadmapLevel? Level,
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
    // REC1-B2 — KHÔNG CÒN ĐƯỢC ĐỌC (ValidateCurrentLevel + currentLevelOverride đã gỡ khỏi
    // RoadmapService.CreateAsync). "Trình độ hiện tại" đi xuống AI CHỈ còn suy từ `cv_analyses`
    // qua `CvAnalysisId` — client gửi gì vào field này cũng bị bỏ qua, không 400, không tác dụng.
    // Giữ field lại (không xoá) để call site cũ dùng named argument không vỡ biên dịch.
    string? CurrentLevel = null
);

// Điểm yếu gửi xuống AIService /generate-roadmap (khớp WeaknessScore: criterionName + percentage +
// weakSessions + totalSessions — REC1-B1).
// MIS1-B4 — CriterionIds: MỌI id rubric_criteria từng đứng sau CriterionName này trong các buổi đã
// chọn (KHÔNG chỉ id của buổi mới nhất). rubric_criteria có Version + custom-per-candidate: đổi
// version hoặc rubric giữa các buổi sinh ra id MỚI cho "cùng một tên" ⇒ lấy 1 id sẽ âm thầm bỏ sót
// mistake của những buổi mang id khác. Nullable vì record cũ (RoadmapLessonService.cs dựng từ
// Baseline — Dictionary<string,decimal> không mang id) không có id để gắn. KHÔNG dùng để gửi AI
// (AiServiceRoadmapGenerator.cs project criterionName+percentage+weakSessions+totalSessions — id
// KHÔNG rò ra ngoài).
// REC1-B1 — Percentage là "% ở buổi MỚI NHẤT" (baseline hiện tại, không đổi — xem RoadmapService).
// WeakSessions/TotalSessions là trục THỜI GIAN riêng: bao nhiêu trong SỐ buổi đã chọn từng đánh dấu
// tiêu chí này NeedsImprovement, trên tổng bao nhiêu buổi có điểm — "yếu 3/4 buổi" khác hẳn "yếu
// 3/12 buổi" dù Percentage giống nhau. THAM SỐ CUỐI + có default để mọi call site cũ (positional,
// 2-3 tham số) không vỡ.
public record RoadmapWeakness(
    string CriterionName,
    decimal Percentage,
    IReadOnlyCollection<Guid>? CriterionIds = null,
    int WeakSessions = 0,
    int TotalSessions = 0);

// MIS1-B4 — 1 mục "vì sao sai / sửa sao" AI sinh khi mở lesson (MIS1-B3), khớp mistake_key của
// RoadmapMistake/Milestone.MistakeRefs. Shape giống MistakeReviewItem bên AIService NHƯNG là type
// ĐỘC LẬP — không share, hai bên chỉ khớp nhau qua hợp đồng JSON.
public record LessonMistakeReviewItem(string MistakeId, string WhatWentWrong, string HowToFixIt);

// MIS1-B7 — hình chiếu ra CLIENT của 1 RoadmapMistake cho LessonResponse.Mistakes. KHÁC hẳn
// LessonMistakeReviewItem ngay trên (đó là hợp đồng dây NỘI BỘ .NET↔Python — 3 trường model tự
// trả về): record này là 8 trường FE cần để hiển thị, gồm cả câu hỏi/câu trả lời GỐC (đã có sẵn
// trong RoadmapMistake, không phải chờ AI sinh) chứ không chỉ phần nhận xét AI mới thêm.
//
// `Id` = RoadmapMistake.MistakeKey ("m1".."m12"), KHÔNG PHẢI RoadmapMistake.Id (Guid) — FE dùng
// giá trị này làm khoá đối chiếu với MistakeReview (cùng namespace mistake_key xuyên suốt B4→B7).
//
// `WhatWentWrong`/`HowToFixIt` nullable CÓ CHỦ ĐÍCH: bài đọc lại (chưa mở lần nào sau bản B7, hoặc
// mở trước khi AI trả review) chưa có 2 trường này — vẫn trả 6 trường còn lại thay vì bỏ cả mục,
// vì câu hỏi/câu trả lời/đáp án mẫu tự nó đã có giá trị với người học.
public record LessonMistakeResponse(
    string Id,
    string CriterionName,
    decimal ScorePct,
    string Question,
    string Answer,
    string? WhatWentWrong,
    string? HowToFixIt,
    string? SampleAnswer);

// MIS1-B5 — hình chiếu GỬI của 1 RoadmapMistake dùng RIÊNG cho LessonContext.Mistakes: khác 2
// endpoint kia (/generate-roadmap, /generate-lesson-theory) vốn chiếu bằng ANONYMOUS TYPE ngay
// tại chỗ dựng payload (AiServiceRoadmapGenerator), record này phải đi QUA NHIỀU LỚP có chữ ký
// tường minh (RoadmapLessonService.BeginSessionAsync → PracticeService → AiServiceQuestionGenerator)
// nên cần một type có tên. CỐ Ý KHÔNG có SampleAnswer (ĐÁP ÁN) — loại khỏi TYPE thay vì chỉ loại
// khỏi payload, để không có đường nào lỡ tay serialize nó xuống /generate-questions (CẤM tuyệt đối).
// ScorePct/Answer optional — /generate-questions không cần (chỉ id/criterionName/question/
// reasoning ra JSON, xem AiServiceQuestionGenerator), giữ ở đây cho record đủ hình dạng.
public record RoadmapMistakeWire(
    string Id, string CriterionName, string Question, string Reasoning,
    decimal? ScorePct = null, string? Answer = null);

// BE-5 — bằng chứng HÀNH VI cho 1 tiêu chí YẾU: Reasoning (E11, luôn trích NGUYÊN VĂN lời ứng
// viên) của 2-3 answer điểm THẤP NHẤT — đã tải + cắt trần sẵn (RoadmapEvidenceLoader). Khớp
// WeaknessScore ở chỗ criterionName là snapshot tên (không phải id — rubric có thể đổi version),
// nhưng mang thêm chẩn đoán CỤ THỂ thay vì chỉ con số %.
public record CriterionEvidence(string CriterionName, IReadOnlyList<string> Reasoning);

// Kết quả AI /generate-roadmap (sync) — chỉ cấu trúc (title/focusCriteria/lessons.title), không điểm.
// MIS1-B5 — MistakeIds: mistake_key (MIS1-B2 gom chủ đề) model tự gán, CHƯA lọc theo id thật —
// RoadmapService.CreateAsync PHẢI narrow lại trước khi lưu (CẤM: tin thẳng AI). Mặc định null =
// model không trả field này (caller cũ / AIService bản cũ không biết field này).
public record RoadmapGenAiResult(IReadOnlyList<GeneratedMilestone> Milestones);
public record GeneratedMilestone(
    string Title, IReadOnlyList<string> FocusCriteria, IReadOnlyList<GeneratedLesson> Lessons,
    IReadOnlyList<string>? MistakeIds = null);
public record GeneratedLesson(string Title, IReadOnlyList<string>? MistakeIds = null);

// { criterionName, deltaPct } — set khi milestone Completed (BC15); BC12 luôn null.
public record MilestoneImprovementResponse(string CriterionName, decimal DeltaPct);

// F15 — kết quả AIService /generate-lesson-theory: markdown + tài liệu học (đã qua allowlist
// tên miền phía AIService). Resources rỗng KHÔNG phải lỗi.
// RAG grounding — CitedChunkIds: id chunk grounding mà AI THẬT SỰ cite (Contract 2). Rỗng khi không
// truyền grounding / AI không cite → lesson ungrounded.
// MIS1-B5 — MistakeReview: "vì sao sai / sửa sao" cho từng lỗi ĐÃ GỬI (MIS1-B3). CHƯA lọc theo id
// thật — RoadmapLessonService.OpenLessonAsync PHẢI narrow lại trước khi lưu (CẤM: tin thẳng AI).
// null = không gửi mistakes cho lượt này (caller cũ / AIService bản cũ không biết field này).
public record LessonTheoryResult(
    string TheoryMarkdown,
    IReadOnlyList<Entities.LessonResource> Resources,
    IReadOnlyList<string>? CitedChunkIds = null,
    IReadOnlyList<LessonMistakeReviewItem>? MistakeReview = null);

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
    bool CanRetry = false,

    // MIS1-B7 — mục lỗi ĐẦY ĐỦ (câu hỏi/câu trả lời gốc + "vì sao sai / sửa sao" khi đã có review;
    // RoadmapLessonService đã NARROW review theo id thật, không tin thẳng AI). THAM SỐ CUỐI,
    // OPTIONAL — additive, không đụng caller cũ.
    // null  = bài không bám lỗi nào (RoadmapLessonService.ResolveLessonMistakes rỗng).
    // []    = bám lỗi nhưng không nạp được hàng nào từ DB (dữ liệu lệch — không nên xảy ra).
    // [...] = có lỗi để hiển thị; whatWentWrong/howToFixIt từng phần tử có thể null (chưa có review).
    // KHÔNG cắt độ dài ở server — FE tự quyết định hiển thị bao nhiêu.
    IReadOnlyList<LessonMistakeResponse>? Mistakes = null
);

public record MilestoneResponse(
    Guid Id,
    int OrderNo,
    string Title,
    IReadOnlyList<string> FocusCriteria,
    string Status,
    IReadOnlyList<MilestoneImprovementResponse>? Improvement,
    IReadOnlyList<LessonResponse> Lessons,

    // MIS1-B5 — số lỗi (MIS1-B2 gom chủ đề) chặng này bám, ĐÃ NARROW theo id thật. 0 = chặng không
    // bám lỗi nào (roadmap cũ trước MIS1-B4, hoặc buổi không có lỗi để gom).
    int MistakeCount = 0
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
    // cùng nghề + cùng level nằm cạnh nhau thì "ôn tập" hay "tiến lên" từng là thứ DUY NHẤT phân
    // biệt được chúng (đúng lập luận đã dùng cho `Name` ở BE-6).
    //
    // 🔴 MIS1-B6 — câu trên nay SAI: Guard 1/2/3 mới (RoadmapService.CreateAsync) buộc CẢ HAI mode
    // đều xây từ cùng một loại dữ liệu (buổi luyện đã chấm + lỗi nội dung trích được), nên khác biệt
    // hành vi thật sự giữa chúng đã thu hẹp lại chỉ còn CÁCH DIỄN GIẢI của prompt (mục tiêu hướng
    // lên vs giữ nguyên trình độ đang đứng). `Mode` vẫn LƯU đúng — chỉ là NHÃN LỊCH SỬ của dữ liệu
    // đã có từ trước MIS1-B6, không còn là "thứ duy nhất phân biệt" theo nghĩa hành vi tạo lộ trình.
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
