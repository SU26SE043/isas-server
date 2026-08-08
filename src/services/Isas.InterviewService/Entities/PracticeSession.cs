using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class PracticeSession : IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    // Tham chiếu lỏng sang AuthService (candidate) - không FK xuyên service
    public Guid CandidateId { get; set; }

    // Phân biệt B2B/B2C: null = B2C luyện tập; có giá trị = bài thi của 1 campaign.
    // Ref lỏng sang CampaignService - KHÔNG FK xuyên service (architecture §5).
    public Guid? CampaignId { get; set; }

    // FK cứng tới FileRecord (B2C: file_records nằm chung interview DB)
    public Guid? CvId { get; set; }
    public Guid? JdId { get; set; }
    public JobCategory JobCategory { get; set; }

    // Snapshot per session: adaptive questions, scoring jobs and TTS must use the language chosen
    // at creation time, never a mutable runtime default.
    public string Language { get; set; } = "vi";
 
    public SessionStatus Status { get; set; } = SessionStatus.GeneratingQuestions;
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // DB14 — audit: đóng dấu mỗi lần session bị sửa (status flip, chấm xong, abandon...). C# init giống
    // CreatedAt (Interview CreatedAt do C# gán, không dùng DB now()) để insert SQLite/EnsureCreated chạy;
    // config cũng đặt default now() ở DB. Stamp tự động khi Modified (SaveChanges override); flip qua
    // ExecuteUpdate (SessionAbandonSweeper, SessionScoringNotifier) tự .SetProperty(UpdatedAt).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // I2 (D21) — hạn chót NHẬN BÀI của cả buổi (KHÔNG phải giới hạn tổng thời gian làm bài):
    // B2B = campaigns.expires_at (Campaign gửi kèm lúc tạo session); B2C = null (không hard-deadline,
    // chỉ giới hạn từng câu qua PracticeQuestion.TimeLimitSec). Quá Deadline + InProgress →
    // SessionAbandonSweeper auto-submit (≥1 answer) hoặc SessionAbandoned (0 answer) — chống reservation treo.
    public DateTime? Deadline { get; set; }

    // BC9 — tổng kết buổi luyện B2C, set khi Scored (campaign_id null); null khi chưa chấm xong / B2B.
    public decimal? OverallScore { get; set; }   // điểm tổng 0–100 (trung bình cộng pct các tiêu chí)
    public int? AnsweredCount { get; set; }        // số câu đã chấm lúc tính kết quả (snapshot)

    // BC10 — nhận xét chung buổi (AI sinh best-effort khi Scored, chỉ B2C); null nếu chưa/AI lỗi/B2B.
    public string? OverallComment { get; set; }

    // Phỏng vấn THÍCH ỨNG — bật/tắt vòng lặp câu-kế-động cho buổi này (đóng dấu lúc tạo session từ cấu
    // hình B2C `Adaptive:Enabled` hoặc field campaign B2B). Tắt (mặc định) → giữ nguyên luồng batch tĩnh.
    public bool AdaptiveEnabled { get; set; }

    // Trần số câu hỏi thích ứng được thêm cho CẢ BUỔI (0 = không trần cứng). Chống buổi kéo dài vô tận.
    // ⚠ INT-17b: ở chế độ chuỗi-theo-câu, trần này để 0 — nếu không nó bó chặt hơn trần theo câu
    // (5 gốc × 3 = 15 câu sâu) và tính năng sẽ chết ở câu đào sâu thứ 3. `MaxQuestions` là trần buổi.
    public int MaxFollowUps { get; set; }

    // INT-17b — trần số câu ĐÀO SÂU cho MỖI câu gốc. 0 = chế độ CŨ (frontier: chỉ sinh câu kế khi mọi
    // câu đã trả lời, ngân sách tính theo buổi) ⇒ vừa là kill-switch vừa là bộ chọn chế độ, đổi được
    // lúc chạy chứ không cần deploy lại. Row cũ + campaign cũ mặc định 0 nên hành vi không đổi.
    public int MaxDeepPerQuestion { get; set; }

    // INT-17b — số lần gọi `/decide-next` lỗi trong buổi này. Chế độ chuỗi gọi AI sau gần như MỌI câu
    // trả lời, mà mỗi lần lỗi vẫn phải chờ hết timeout ⇒ AIService hỏng sẽ cộng hàng chục phút chờ chết
    // vào đúng một buổi thi. Chạm `Adaptive:MaxFailuresPerSession` → thôi gọi, degrade về luồng tĩnh.
    public int AdaptiveFailures { get; set; }

    // Trần TỔNG số câu hỏi của buổi (seed + thích ứng; 0 = không trần cứng). B2B: giữ độ dài so sánh được.
    // F2b — CHECK `max_questions BETWEEN 0 AND 20`: trần ở tầng service chặn được đường HTTP, nhưng
    // đường internal (Campaign gọi thẳng) thì không → chốt thêm ở DB để không có đường nào vượt.
    public int MaxQuestions { get; set; }

    // T7 — entitlement is resolved once at B2C session creation. Existing sessions retain legacy defaults.
    public string EntitlementSource { get; set; } = "legacy";
    public string TierCode { get; set; } = "free";
    public int TierRank { get; set; }
    public bool GroundingEnabled { get; set; }
    public int SelfConsistencyN { get; set; } = 1;
    public bool CvAnalysisIncluded { get; set; }
    public bool RepoAnalysisIncluded { get; set; }
    public bool RoadmapEnabled { get; set; }

    // Con dấu PHẠM VI CHẤM của buổi — trả lời đúng một câu: "điểm buổi này tính trên TOÀN BỘ rubric,
    // hay trên tập tiêu chí riêng của từng câu hỏi?". Cần vì việc thu hẹp phạm vi làm điểm KHÔNG CÒN
    // SO SÁNH ĐƯỢC với điểm cũ, mà BC15 (đo cải thiện) · F14 (mốc peer) · CAMP-10 (xếp hạng) đang
    // đem so thẳng. Tiền lệ: `practice_answers.metrics_version` (F11), `answer_scores.prompt_version` (BK23).
    //
    //   null = KHÔNG BIẾT — row có trước khi cột này tồn tại. ⚠ KHÔNG được suy ra "khác phiên bản"
    //          từ null (nguyên tắc BK23: suy "khác" từ "không biết" là bịa). Trên thực tế row null
    //          đều là phạm vi cũ, nhưng đó là suy đoán của người đọc, không phải điều dữ liệu khẳng định.
    //      1 = ĐÃ BIẾT: chấm trên toàn bộ rubric (không câu nào mang nhãn tiêu chí) — B2B, rubric
    //          riêng BC16 không phân loại, hoặc AIService không gắn được nhãn. So với null: cùng
    //          hành vi, chỉ khác ở chỗ 1 là ghi nhận còn null là không biết.
    //      2 = ĐÃ BIẾT: buổi có ít nhất một câu mang nhãn ⇒ tồn tại answer được chấm trên tập tiêu chí
    //          HẸP HƠN rubric. Chỉ giá trị này mới CHỨNG MINH được "khác thước đo".
    //
    // Đóng dấu ở tầng BUỔI (không phải từng dòng điểm) vì (a) mọi phép so ở trên đều đọc số liệu tổng
    // hợp mức buổi, (b) `answer_scores` là bảng lớn nhất hệ (~100M dòng ở quy mô mục tiêu) nên thêm
    // cột ở đó là cái giá không mua lại được gì.
    public int? ScoringScopeVersion { get; set; }

    // F2 — thời lượng cho MỖI câu của buổi này (giây), ứng viên chọn lúc tạo (60/120/240).
    // Vì sao lưu trên SESSION chứ không chỉ trên từng câu: câu THÍCH ỨNG được sinh SAU lúc tạo session
    // (AnswerService), lúc đó không còn đường nào biết ứng viên đã chọn gì nếu không đọc lại từ đây.
    // Default 120 = hành vi cũ ⇒ row cũ + B2B (chưa cho chọn) không đổi gì.
    public int TimeLimitSec { get; set; } = 120;

    // Navigation
    public ICollection<PracticeQuestion> Questions { get; set; } = [];
    public ICollection<PracticeAnswer> Answers { get; set; } = [];
    public ICollection<SessionCriterionScore> CriterionScores { get; set; } = [];   // BC9 (B2C)
}
