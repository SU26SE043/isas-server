namespace Isas.CampaignService.DTOs
{
    /// <summary>CAMP-19 — <c>POST /campaign/{id}/rubric-preview</c>.</summary>
    public class RubricPreviewRequest
    {
        /// <summary>Câu hỏi HR chọn để chấm thử. null = lấy câu đầu tiên của chiến dịch.</summary>
        public Guid? QuestionId { get; set; }

        /// <summary>
        /// Bài thứ 4 do HR tự dán (tuỳ chọn) — bài DUY NHẤT không do chính bộ chấm viết ra, nên là
        /// đối chứng duy nhất cho độ chệch tự-khen-văn-mình.
        /// </summary>
        public string? CustomAnswer { get; set; }

        /// <summary>
        /// REV-BE R3 (hợp đồng W1, khoá JSON <c>confirmBilled</c>, mặc định <c>false</c>) — client XÁC NHẬN chấp
        /// nhận trừ 1 credit tổ chức nếu quota free của (campaign, rubricVersion, câu) đã hết. Lượt SẼ tính
        /// phí mà cờ này <c>false</c> ⇒ <b>409</b> <c>{ code: "PREVIEW_BILLING_CONFIRM_REQUIRED",
        /// freeRunsRemaining: 0, questionId }</c> — ném TRƯỚC khi insert row và TRƯỚC ReserveAsync (I7).
        /// Lượt free ⇒ cờ không cần.
        /// </summary>
        public bool ConfirmBilled { get; set; }
    }

    public class RubricPreviewRunResponse
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = null!;          // Running | Succeeded | Failed
        public Guid? QuestionId { get; set; }
        public string QuestionText { get; set; } = null!;

        // Badge "cùng thước đo / khác thước đo" khi HR so hai lượt. Thiếu ba thứ này thì HR quy mọi
        // thay đổi điểm cho việc mình vừa sửa mốc ⇒ kết luận sai.
        public string RubricFingerprint { get; set; } = null!;
        public int RubricVersion { get; set; }
        public int? PromptVersion { get; set; }

        /// <summary>
        /// v1 LUÔN false: bài mẫu là văn bản nên không có số đo cách nói (F11). CỐ Ý là cờ cấu trúc
        /// chứ không loại tiêu chí "trôi chảy" khỏi lượt chấm — bỏ một tiêu chí sẽ đổi điểm CÁC TIÊU
        /// CHÍ CÒN LẠI (rubric_block đổi) và đổi cả mẫu số điểm tổng (INT-10), mà nhận diện tiêu chí
        /// nào là "trôi chảy" chỉ có thể làm bằng khớp tên — heuristic chắc chắn bắn nhầm vì tên do
        /// HR gõ và có cả hai ngôn ngữ.
        /// </summary>
        public bool DeliveryMetricsAvailable { get; set; }

        public bool LengthParityWarning { get; set; }

        public bool Billed { get; set; }
        /// <summary>SC2 · T6 (D-4) — theo (campaign, rubricVersion, questionId); <c>FreeRunsPerQuestion = 1</c>.</summary>
        public int FreeRunsRemaining { get; set; }

        /// <summary>Bộ thước đo ĐÃ DÙNG (snapshot), không phải bộ hiện tại — ĐỦ bộ, FE lọc bằng <see cref="ScopedCriterionIds"/>.</summary>
        public List<RubricPreviewCriterion> Rubric { get; set; } = new();

        /// <summary>
        /// SC2 · T6 — tập tiêu chí ĐÃ CHẤM trong lượt này = phạm vi của câu (Always ∪ nhãn câu; câu chưa gắn
        /// nhãn ⇒ toàn bộ). Lượt trước T6 (snapshot không có InScope) ⇒ toàn bộ. <c>samples[].scores</c> chỉ
        /// chứa tiêu chí trong tập này.
        /// </summary>
        public List<Guid> ScopedCriterionIds { get; set; } = new();

        public List<RubricPreviewSample> Samples { get; set; } = new();

        public string? ErrorReason { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class RubricPreviewCriterion
    {
        public Guid CriterionId { get; set; }
        public string Name { get; set; } = null!;
        public decimal Weight { get; set; }
        public int MaxScore { get; set; }
        /// <summary>SC2 · T6 — "Always" | "WhenTargeted"; null = snapshot trước T6.</summary>
        public string? ScoringScope { get; set; }
        /// <summary>
        /// SC2 · T6 — tiêu chí này có được CHẤM trong lượt không. <c>null</c> = snapshot trước T6 (không có
        /// trường) ⇒ coi là in-scope (lượt cũ chấm toàn bộ — I5). KHÔNG migration: nằm trong jsonb snapshot.
        /// </summary>
        public bool? InScope { get; set; }
        public List<CriterionLevelResponse> Levels { get; set; } = new();
    }

    public class RubricPreviewSample
    {
        public string Band { get; set; } = null!;    // Weak | Good | Excellent | Custom
        public string AnswerText { get; set; } = null!;
        public int WordCount { get; set; }

        // Mức kỳ vọng do CODE chọn trước khi sinh bài ⇒ so được với điểm thật. Δ dương có hệ thống =
        // model đang tự khen văn nó viết. Đây là số đo duy nhất về độ chệch mà một model đơn cho được.
        public decimal ExpectedWeightedPct { get; set; }
        public decimal ActualWeightedPct { get; set; }

        public List<RubricPreviewSampleScore> Scores { get; set; } = new();
    }

    public class RubricPreviewSampleScore
    {
        public Guid CriterionId { get; set; }
        public string CriterionName { get; set; } = null!;
        public int MaxScore { get; set; }
        public int ExpectedLevel { get; set; }
        public decimal ActualScore { get; set; }
        public int? LevelMatched { get; set; }
        public string? Reasoning { get; set; }
    }
}
