namespace Isas.InterviewService.DTOs;

using System.ComponentModel.DataAnnotations;
using Isas.InterviewService.Enums;

// jobCategory BẮT BUỘC — tín hiệu tối thiểu để sinh câu hỏi. Kiểu nullable để phân biệt "thiếu"
// với default enum BA (value 0): thiếu → 400 (Required cho model-binding HTTP + guard service TRƯỚC
// reserve, xem PracticeService.CreateSessionInternalAsync) → KHÔNG giữ credit oan (PAY-5). Trước đây
// non-nullable không [Required] → omitted im lặng thành BA(0) VÀ vẫn reserve 1 credit (B2C audit P1).
// ⚠ Attribute phải nằm trên PARAMETER (KHÔNG [property:]) — ASP.NET (.NET 10) THROW khi validation
// attribute property-targeted trên positional record → 500 mọi request (mẫu CvAnalysisRequest/BK6).
// JdText: JD nhập THẲNG dạng text (khỏi phải upload PDF trước) — mượn nguyên quy ước C11 của
// B2B/Campaign (`jdText` + "text ưu tiên file") để 2 dòng sản phẩm nhất quán. Gửi cả `jdText` lẫn
// `jdId` → TEXT THẮNG, bỏ file (xem PracticeService.CreateSessionInternalAsync). Đặt CUỐI + có
// default → mọi call site positional cũ (RoadmapLessonService, test) không phải sửa.
// TimeLimitSec (F2): thời lượng mỗi câu ứng viên chọn — 60/120/240; null = 120 (hành vi cũ).
// ⚠ Đặt CUỐI + có default: call site positional (RoadmapLessonService, test cũ) không phải sửa.
public record CreatePracticeSessionRequest(
    Guid? CvId,        // optional
    Guid? JdId,        // optional
    [Required] JobCategory? JobCategory,
    string? JdText = null,     // optional — ưu tiên hơn JdId
    int? TimeLimitSec = null,  // optional — 60/120/240; null = mặc định 120
    int? QuestionCount = null, // optional — 1..20; null = mặc định của AIService (5)
    string? Language = null,
    // Seniority: mức ứng viên tự khai (Fresher|Junior|Middle|Senior), đóng dấu lên session.
    // ⚠ NULLABLE có chủ ý — xem PracticeService.ValidateSeniority: null (client cũ không gửi) =
    // "Junior", còn chuỗi RỖNG là một giá trị SAI → 400. Kiểu non-nullable `= "Junior"` KHÔNG phân
    // biệt được hai ca đó: System.Text.Json vẫn bind `"seniority": null` thành null (nullable ref
    // types không được enforce lúc chạy) ⇒ nhánh mặc định không bao giờ với tới được, client gửi
    // null nhận 400 thay vì Junior.
    string? Seniority = null,
    // ── Ứng viên tự chọn CHẾ ĐỘ và ĐỘ SÂU (đặt CUỐI + có default: call site positional cũ không đổi)
    //
    // AdaptiveEnabled: null = "không có ý kiến" ⇒ giữ nguyên cấu hình server. `false` = ứng viên xin
    // buổi TĨNH, nhận đúng số câu đã chọn, không câu chèn. Ngữ nghĩa CHỈ-CHO-TỪ-CHỐI (xem
    // ResolveSessionSettings): gửi `true` khi admin/gói đã tắt thì KHÔNG bật lên được — cấu hình
    // admin là trần, không phải gợi ý.
    bool? AdaptiveEnabled = null,
    // MaxDeepPerQuestion: số câu đào sâu tối đa MỖI câu gốc. null = giữ mặc định server.
    // ⚠ Dải hợp lệ là 1..cấu-hình, KHÔNG nhận 0: `0` không có nghĩa "tắt đào sâu" mà là BỘ CHỌN
    // CHẾ ĐỘ (chế độ frontier cũ — vẫn có câu chèn, chỉ dồn ở đuôi buổi; xem PracticeSession.
    // MaxDeepPerQuestion). Muốn tắt phải gửi AdaptiveEnabled=false. Nhận 0 ở đây là mở đường cho
    // UI diễn đạt "tắt" bằng một giá trị đổi hẳn thuật toán mà không ai biết.
    int? MaxDeepPerQuestion = null
);

// SC3 — tất cả số liệu nghiệp vụ (đặc biệt SeedCount) do server tính bằng đúng luật tạo session.
// UI chỉ dùng QuestionCount × TimeLimitSec để hiển thị thời lượng.
public record PracticeSessionOptionsResponse(
    bool AdaptiveEnabled,
    int MaxDeepPerQuestion,
    int ContentCriteriaCount,
    int QuestionCountMin,
    int QuestionCountMax,
    int DefaultQuestionCount,
    IReadOnlyList<PracticeSessionPreset> Presets,
    IReadOnlyList<PracticeSessionPreview> Preview,
    // Dải độ sâu ứng viên được chọn. PHẢI trả về, và phải bằng ĐÚNG dải mà server dùng để từ chối:
    // repo đã dính một lần lỗi "trần báo cho UI khác trần dùng để từ chối" với questionCount, triệu
    // chứng là UI cho bấm rồi server trả 400 — người dùng không hiểu mình sai ở đâu.
    // Adaptive tắt ⇒ cả hai bằng 0 ⇒ UI ẩn ô chọn.
    int MaxDeepPerQuestionMin,
    int MaxDeepPerQuestionMax);

public record PracticeSessionPreset(
    string Key,
    int QuestionCount,
    int SeedCount,
    bool CoversAllCriteria);

public record PracticeSessionPreview(int QuestionCount, int SeedCount);

// I1 (B2B): Campaign gửi tiêu chí CÓ CẤU TRÚC kèm khi tạo session → materialize thành rubric_criteria(campaign_id).
/// <summary>
/// Một MỐC ĐIỂM của tiêu chí campaign (E9 hard-anchor): điểm này nghĩa là ứng viên đã làm/nói được gì.
/// Map 1-1 sang <c>rubric_levels</c> lúc materialize.
/// </summary>
public record CampaignCriterionLevelInput(int Score, string Descriptor);

public record CampaignCriterionInput(
    string Name,
    string? Description,
    decimal Weight,    // Σ/campaign = 1 (chuẩn hoá phía Campaign)
    int MaxScore,
    // E9 — mốc điểm HR soạn (AI gợi ý rồi HR sửa). null/rỗng = không có mốc ⇒ AIService rơi về dải
    // mặc định 0..maxScore như trước, KHÔNG phải lỗi. Optional ở CUỐI record để bản Campaign cũ —
    // chưa biết field này — vẫn gọi được endpoint mà không vỡ (hai service deploy không nguyên tử).
    IReadOnlyList<CampaignCriterionLevelInput>? Levels = null
);

// I1 (B2B): tạo session bài thi của 1 campaign. Câu hỏi + tiêu chí do Campaign cấp (không gọi AI sinh).
// I2: ExpiresAt = hạn chót nhận bài (campaigns.expires_at) → set session.Deadline; null = không hard-deadline.
// Phỏng vấn THÍCH ỨNG (B2B): Adaptive*/MaxFollowUps/MaxQuestions do Campaign/HR bật (optional; null = tắt).
// Seed = toàn bộ campaign questions (ai cũng nhận) → câu thích ứng thêm ở đuôi, chấm theo CÙNG tiêu chí.
/// <summary>Một câu campaign kèm đáp án mẫu HR soạn (null = chưa soạn).</summary>
public record CampaignQuestionInput(string Text, string? SampleAnswer = null);

public record CreateCampaignSessionRequest(
    Guid CampaignId,
    Guid OrgId,        // BK14: chủ ví credit (owner=Org) để reserve khi tạo session B2B (PAY-6)
    JobCategory JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    DateTime? ExpiresAt = null,
    bool? AdaptiveEnabled = null,
    int? MaxFollowUps = null,
    int? MaxQuestions = null,
    // INT-17b — trần đào sâu MỖI câu campaign (null/0 = chế độ cũ: đào sâu dồn ở đuôi buổi).
    int? MaxDeepPerQuestion = null,
    string? Language = null,
    // Nullable — cùng hợp đồng với CreatePracticeSessionRequest.Seniority (null = Junior, rỗng = 400).
    string? Seniority = null,
    // Phiên bản bộ tiêu chí do CampaignService cấp (campaigns.rubric_version). Interview chỉ CHÉP —
    // xem ghi chú ở PracticeSession.CampaignRubricVersion. null (Campaign bản cũ) => 1, tức khớp mọi
    // row đang có trên prod ⇒ hành vi giống hệt trước thay đổi này.
    int? RubricVersion = null,
    // Câu hỏi KÈM đáp án mẫu. Cố ý là field RIÊNG chứ không đổi kiểu `Questions` sẵn có: hai service
    // deploy không nguyên tử, nên trong cửa sổ giữa hai lần khởi động phải có một bản Campaign mới nói
    // chuyện được với bản Interview cũ và ngược lại. Campaign gửi CẢ HAI; Interview ưu tiên field này,
    // vắng thì rơi về `Questions`. Gỡ `Questions` là việc của một đợt sau, khi cả hai bên đã lên.
    // ⚠ Nếu có thì SỐ LƯỢNG và THỨ TỰ phải khớp `Questions` — Interview không tự ghép lại.
    IReadOnlyList<CampaignQuestionInput>? QuestionDetails = null,
    // SCP1 · B5 — hợp đồng chấm điểm (chính sách biểu thức) của campaign, ghim vào practice_sessions.
    // Cả 4 nullable + CUỐI record. null = Campaign chưa áp chính sách (dùng weighted mặc định).
    int? CampaignPolicyVersion = null,
    string? CampaignPolicyExpression = null,
    int? CampaignPolicyPassScorePct = null,
    string? CampaignPolicyEngineVersion = null,
    // RNK1 · HĐ-2 / CAMP-21 — campaigns.skip_penalty. null (bản Campaign cũ chưa gửi) ⇒ session
    // skip_penalty = false (không phạt). Optional ở CUỐI record.
    bool? SkipPenalty = null
);

// D2: request cho endpoint internal create-or-get session B2B (CampaignService gọi khi ứng viên bấm
// "Start Interview"). candidateId đi kèm (Campaign đã provision qua Auth); jobCategory là STRING để
// TryParse mềm (ref lỏng xuyên service — Campaign gửi Domain, không lệ thuộc enum Interview).
public record CreateCampaignSessionInternalRequest(
    Guid CandidateId,
    Guid CampaignId,
    Guid OrgId,        // BK14: chủ ví credit org (Campaign gửi campaign.OrgId) → reserve owner=Org (PAY-6)
    string JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    // I2: hạn chót nhận bài (campaigns.expires_at). Campaign gửi kèm → set session.Deadline; null =
    // không hard-deadline (chỉ giới hạn từng câu). Campaign gửi field này là FOLLOW-UP nhỏ ngoài scope I2.
    DateTime? ExpiresAt = null,
    // Phỏng vấn THÍCH ỨNG (B2B): Campaign/HR bật toggle + trần (optional; null = tắt → luồng batch tĩnh cũ).
    bool? AdaptiveEnabled = null,
    int? MaxFollowUps = null,
    int? MaxQuestions = null,
    // INT-17b — trần đào sâu MỖI câu campaign (null/0 = chế độ cũ). Field optional CUỐI record →
    // client cũ (chưa gửi) không vỡ.
    int? MaxDeepPerQuestion = null,
    string? Language = null,
    // Nullable — cùng hợp đồng với CreatePracticeSessionRequest.Seniority (null = Junior, rỗng = 400).
    // Campaign gửi field này; client cũ chưa gửi vẫn ra Junior thay vì 400.
    string? Seniority = null,
    // Câu hỏi KÈM đáp án mẫu (xem CreateCampaignSessionRequest.QuestionDetails). Optional ở CUỐI record
    // để bản Campaign cũ — chưa biết field này — vẫn gọi được endpoint mà không vỡ.
    IReadOnlyList<CampaignQuestionInput>? QuestionDetails = null,
    // Phiên bản bộ tiêu chí (campaigns.rubric_version). Khoá JSON trên dây: `rubricVersion`
    // (JsonSerializerDefaults.Web ⇒ camelCase). null = Campaign bản cũ ⇒ Interview coi là 1.
    int? RubricVersion = null,
    // SCP1 · B5 — HỢP ĐỒNG CHẤM ĐIỂM (chính sách biểu thức) của campaign, ghim vào practice_sessions
    // lúc tạo. Cả 4 nullable + ở CUỐI record: bản Campaign cũ chưa gửi ⇒ null ⇒ buổi dùng công thức
    // weighted mặc định (hành vi trước SCP1). Ghim CẢ biểu thức — Interview không đọc được bảng
    // scoring_policies của Campaign lúc chấm/preview.
    int? CampaignPolicyVersion = null,
    string? CampaignPolicyExpression = null,
    int? CampaignPolicyPassScorePct = null,
    string? CampaignPolicyEngineVersion = null,
    // RNK1 · HĐ-2 / CAMP-21 — campaigns.skip_penalty (khoá JSON trên dây: `skipPenalty`, camelCase
    // Web). null (bản Campaign cũ) ⇒ session.skip_penalty = false ⇒ không phạt. Optional ở CUỐI record.
    bool? SkipPenalty = null
);
public record PracticeSessionResponse(
    Guid Id,
    string Status,
    string JobCategory,
    string Language,
    Guid? CvId, Guid? JdId,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<QuestionResponse> Questions,
    SessionResultResponse? Result = null,  // BC9 — chỉ khi status=Scored & campaign_id=null (B2C); null nếu chưa
    string Seniority = "Junior",           // snapshot mức do ứng viên chọn khi tạo B2C session
    IReadOnlyList<CriterionEvidenceResponse>? CriterionEvidence = null,
    // TOP1-B5 — danh mục đề tài gắn cho buổi (snapshot lúc tạo). null = tắt tính năng / buổi bài
    // học lộ trình / pool rỗng / buổi cũ trước cột này tồn tại — client cũ (không biết field) không
    // vỡ. Đặt CUỐI + có default (mẫu CriterionEvidence ngay trên): call site positional cũ không
    // phải sửa.
    IReadOnlyList<SessionTopicResponse>? Topics = null
);

// Evidence state được trả dạng additive ở GET session để client khôi phục đúng ngữ cảnh đã dùng
// cho lượt adaptive tiếp theo; null = session cũ/B2B chưa bật evidence tracking.
public record CriterionEvidenceResponse(
    Guid CriterionId,
    string CriterionName,
    string State,
    IReadOnlyList<string> EvidenceFound,
    IReadOnlyList<string> MissingEvidence,
    int DeepCount,
    DateTime UpdatedAt
);

public record QuestionResponse(
    Guid Id,
    int OrderNo,
    string Content,
    int TimeLimitSec,
    AnswerResponse? Answer,
    string Kind = "Seed",   // phỏng vấn THÍCH ỨNG — Seed | FollowUp | Clarify | NewQuestion (default an toàn cho client cũ)
    // RAG grounding — nguồn ĐÃ RESOLVE cho câu hỏi này ({chunkId, sourceUrl, sourceTitle}).
    // 3 trạng thái (FE dựa vào — supervisor chốt): null = câu không đi qua grounding (không nhãn);
    // [] = đã grounding nhưng không có nguồn → ungrounded (FE nhãn nổi bật); non-empty = grounded.
    IReadOnlyList<Citation>? Citations = null
);

public record AnswerResponse(
    Guid Id,
    string Status,
    int DurationSec,
    string? Transcript,
    IReadOnlyList<AnswerScoreResponse> Scores,
    bool NeedsReview = false,  // E10 — self-consistency: spread điểm giữa các attempt vượt ngưỡng → cần soi lại (nullable-default → không phá client)
    // F13 (FR07) — câu trả lời MẪU mức tối đa cho ĐÚNG câu hỏi này (AI sinh cùng lượt chấm).
    // null khi chưa chấm xong / LLM không trả → client chỉ đơn giản không hiện mục gợi ý.
    // Đặt CUỐI + có default: client cũ không vỡ (tiền lệ CriterionName/MaxScore).
    string? SampleAnswer = null,
    // F11 (FR06) — chỉ số ĐỘ TRÔI CHẢY đo từ audio (tốc độ nói / khoảng lặng / từ đệm).
    // ⚠ null = CHƯA ĐO ĐƯỢC (answer trước F11 · audio rỗng · đường degrade), KHÁC HẲN "đo ra 0":
    // FE phải hiện "chưa có dữ liệu" chứ đừng hiện "0 từ đệm" như một lời khen.
    DeliveryMetricsDto? DeliveryMetrics = null,
    // URL API owner-scoped để phát/tải bản ghi âm; không bao giờ lộ AudioObjectKey của SeaweedFS.
    string? AudioUrl = null
);

public record AnswerAudioContent(Stream Content, string ContentType);

public record AnswerScoreResponse(
    Guid CriterionId,
    decimal Score,
    string? Reasoning,
    int RubricVersion,
    int? LevelMatched = null,  // E9 — mức khớp khi neo theo rubric_levels; null nếu chưa neo (nullable → không phá client)
    // Tên + thang điểm tiêu chí, để client HIỂN THỊ được mà không phải tra ngược id.
    // Bắt ở e2e 2026-07-18: client chỉ nhận `criterionId` nên breakdown điểm hiện trơ "Điểm tiêu chí"
    // (B2C) và mã GUID (transcript B2B). Tra ngược KHÔNG khả thi: `rubric_criteria` của campaign được
    // mint `Guid.NewGuid()` lúc materialize (PracticeService) nên id này KHÁC id `campaign_criteria`.
    // Nullable + đặt cuối: client cũ không vỡ; caller quên `.ThenInclude(Criterion)` thì ra null chứ
    // không ném NRE.
    string? CriterionName = null,
    int? MaxScore = null
);

public record PracticeSessionSummary(
    Guid Id,
    string Status,
    string JobCategory,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    decimal? OverallScore,   // BC9 — điểm tổng 0–100 nếu đã Scored (B2C); null nếu chưa
    // J8 — cấp độ ứng viên đã chọn CHO ĐÚNG BUỔI ĐÓ (không phải cấp độ hiện tại của người dùng).
    // Cấp độ ảnh hưởng cả câu hỏi (J4) lẫn trọng tâm chấm (J5) ⇒ đổi cấp độ là đổi bối cảnh: biểu
    // đồ tiến bộ so điểm buổi Junior với buổi Senior là so hai thứ khác nhau, không phải "tiến bộ
    // hay tụt lùi". Cột đã có sẵn trên practice_sessions — thêm THUẦN vào projection/DTO, không
    // migration.
    string Seniority,
    /// <summary>
    /// Tên BÀI HỌC của lộ trình mà buổi luyện này thuộc về; <c>null</c> = buổi luyện TỰ DO (không
    /// sinh ra từ một bài học nào).
    ///
    /// <para><b>Vì sao cần:</b> <c>practice_sessions</c> KHÔNG có cột tên/tiêu đề nào, nên bảng
    /// "chọn báo cáo phỏng vấn" của wizard lộ trình chỉ có <c>jobCategory</c> để hiển thị ⇒ mọi buổi
    /// Backend đều hiện đúng một chữ "BE". Đo trên dev: 8 buổi <c>BE|Junior</c> liên tiếp của cùng
    /// một người — không dòng nào phân biệt được với dòng nào.</para>
    ///
    /// <para><b>Nguồn là dữ liệu THẬT, không phải tên máy sinh:</b> <c>roadmap_lesson_attempts</c>
    /// (UNIQUE <c>session_id</c> ⇒ ghép 1–1) → <c>roadmap_lessons.title</c>, ví dụ <i>"Truy vấn SQL
    /// nâng cao (JOIN, GROUP BY) và cơ chế Index cơ bản"</i>. Ghép qua bảng LẦN LÀM chứ không qua
    /// <c>roadmap_lessons.session_id</c>: cột đó chỉ trỏ buổi MỚI NHẤT nên bài đã luyện lại sẽ làm
    /// buổi cũ mất nhãn (đo trên dev: đúng 1 buổi rơi vào ca này). Chiều ngược lại không mất gì —
    /// migration <c>AddRoadmapLessonAttempts</c> đã backfill, đo được 0 buổi có
    /// <c>lessons.session_id</c> mà thiếu dòng lần-làm.</para>
    ///
    /// <para>⚠ <c>null</c> KHÔNG phải lỗi và KHÔNG phải hiếm — đo trên dev 3/18 buổi B2C đã chấm là
    /// luyện tự do. Với nhóm đó hệ thống KHÔNG có nhãn nào để trả; client tự ghép nhãn hiển thị từ
    /// nghề + cấp độ + thời điểm, và tuyệt đối không dựng một cái tên rồi trình bày như tên thật.</para>
    /// </summary>
    string? LessonTitle = null
);

// BC9 — tổng kết cả buổi luyện B2C (số liệu), đọc từ practice_sessions + session_criterion_scores.
public record SessionResultResponse(
    decimal OverallScore,          // 0–100, trung bình cộng pct các tiêu chí (equal weight)
    int AnsweredCount,             // số câu đã chấm (có điểm)
    int TotalQuestions,            // tổng số câu của buổi
    IReadOnlyList<CriterionScoreResponse> CriteriaScores,
    IReadOnlyList<Guid> NeedsImprovement,   // criterionId của tiêu chí dưới ngưỡng
    string? OverallComment = null,  // BC10 — nhận xét chung (AI); null trong BC9
    CvVsAnswerReportResponse? CvVsAnswer = null,  // BC8 — đối chiếu CV↔trả lời; null nếu không có CV đã phân tích
    BenchmarkResponse? Benchmark = null,   // F14 — mốc đối chiếu (lớp 2 của radar); null khi tắt/không dựng được
    /// <summary>
    /// Thước đo đã chấm buổi này: <c>SystemDefault</c> (bộ chuẩn hệ thống) · <c>Custom</c> (rubric
    /// riêng của chính người luyện) · <c>null</c> = KHÔNG BIẾT (buổi có trước cặp cột ghim).
    ///
    /// <para><b>Vì sao phải nói ra:</b> người luyện sửa rubric riêng cho lệch, điểm tụt, và trước đây
    /// không một chữ nào nói rằng họ đang bị chấm bằng thước do CHÍNH HỌ đặt — nên họ kết luận hệ
    /// thống chấm sai.</para>
    ///
    /// <para>⚠ Đọc THẲNG từ hai cột con dấu của buổi, KHÔNG tra lại trạng thái lúc hiển thị: tra lại
    /// là quay về đúng lỗi "hỏi trạng thái lúc chấm" mà con dấu sinh ra để chặn — người vừa lưu rubric
    /// riêng sẽ thấy buổi CŨ của mình bị gắn nhãn sai. Và <c>null</c> giữ nghĩa "không biết", không
    /// bao giờ được vẽ thành <c>SystemDefault</c> (BK23).</para>
    /// </summary>
    string? RubricSource = null,
    int? RubricVersion = null
);

// F14 (FR08) — mốc đối chiếu vẽ chồng lên radar năng lực.
//
// ⚠ `Source`/`Label` là phần QUAN TRỌNG NHẤT của DTO này, không phải `Criteria`. Hệ thống KHÔNG
// có dữ liệu chuẩn ngành; mốc chỉ đến từ (a) trung bình người dùng khác trên chính hệ thống hoặc
// (b) ngưỡng đạt nội bộ. Client PHẢI hiển thị `Label` đúng nguyên văn — tự đặt lại thành "chuẩn
// ngành" là nói dối người dùng về độ tin cậy của đường kẻ họ đang nhìn.
public record BenchmarkResponse(
    string Source,      // PeerAverage | PassThreshold
    string Label,       // nhãn hiển thị, đã nói đúng nguồn (kèm cỡ mẫu khi là PeerAverage)
    int SampleSize,     // số buổi luyện của NGƯỜI KHÁC góp vào (0 khi Source=PassThreshold)
    IReadOnlyList<CriterionBenchmarkResponse> Criteria
);

// F14 — mốc của 1 tiêu chí, thang % để vẽ chung trục với `CriterionScoreResponse.Percentage`.
public record CriterionBenchmarkResponse(
    Guid CriterionId,
    string Name,
    decimal TargetPercentage   // 0–100
);

// BC8 — báo cáo "CV vs câu trả lời": đọc dữ liệu SẴN CÓ (không AI, không call ngoài).
// CvStrengths = strengths (+matched skills) từ cv_analyses (BC7); Gaps = tiêu chí VỪA yếu
// (needs_improvement, BC9) VỪA được CV thể hiện mạnh (token khớp tên tiêu chí ↔ strength CV).
public record CvVsAnswerReportResponse(
    IReadOnlyList<string> CvStrengths,
    IReadOnlyList<CvAnswerGapResponse> Gaps
);

// BC8 — một điểm "CV mạnh nhưng trả lời yếu": tiêu chí answer dưới ngưỡng + bằng chứng CV khớp.
public record CvAnswerGapResponse(
    Guid CriterionId,
    string CriterionName,
    decimal Percentage,          // % điểm answer đạt (dưới ngưỡng cải thiện)
    int MaxScore,
    IReadOnlyList<string> CvEvidence   // strength/skill CV khớp tiêu chí này (giải thích vì sao coi là "CV mạnh")
);

// BC9 — điểm mỗi tiêu chí trong buổi luyện.
public record CriterionScoreResponse(
    Guid CriterionId,
    string Name,
    decimal AverageScore,   // điểm đạt được (TB qua các câu đã chấm)
    int MaxScore,           // điểm tối đa tiêu chí → hiển thị "averageScore/maxScore"
    decimal Percentage,     // averageScore / maxScore × 100 (0–100)
    decimal Weight          // trọng số rubric (B2C chỉ hiển thị, không dùng cho overall)
);
