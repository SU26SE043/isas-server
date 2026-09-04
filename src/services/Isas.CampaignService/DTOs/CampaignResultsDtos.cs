namespace Isas.CampaignService.DTOs
{
    // E5 — bảng kết quả + xếp hạng + pass/fail cho `GET /campaign/{id}/results`.
    // Đọc read-model local `campaign_rankings` (E4 upsert từ event SessionScored) → chỉ chứa ứng viên
    // đã `Scored` (CAMP-11); sắp giảm theo total_score, gán rank (đồng hạng), pass/fail so ngưỡng Employer.
    public class CampaignResultsResponse
    {
        public Guid CampaignId { get; set; }

        // Ngưỡng % Employer (campaigns.pass_score_pct). null = không auto → mọi result = null (HR quyết tay).
        public int? PassScorePct { get; set; }

        public int TotalCandidates { get; set; }

        // CAMP-18 — thước đo ĐANG hiệu lực của chiến dịch. FE so với rubricVersion từng dòng: chỉ một
        // giá trị thì KHÔNG hiện gì (95% chiến dịch — đừng tạo nhiễu); từ hai trở lên mới hiện cột
        // "Thước đo" + băng cảnh báo vì lúc đó bảng đang trộn điểm của hai thước đo khác nhau.
        public int? CurrentRubricVersion { get; set; }

        // RNK1 · HĐ-3 — NGÂN HÀNG ĐỀ: số câu MỖI ứng viên thi (campaigns.questions_per_session; null =
        // thi trọn bộ) + TỔNG số câu trong ngân hàng đề của chiến dịch. FE hiện "K/QuestionBankTotal câu"
        // để HR biết mỗi ứng viên chỉ làm một tập con.
        public int? QuestionsPerSession { get; set; }
        public int QuestionBankTotal { get; set; }

        public List<CampaignResultRow> Results { get; set; } = new();

        // R7 — ứng viên CÓ CỜ chống gian lận nhưng CHƯA `Scored` (bỏ ngang / đang thi). `campaign_rankings`
        // chỉ có row cho ứng viên Scored (CAMP-11) ⇒ đường Results/CSV/PDF cũ giấu mất nhóm này — đúng nhóm
        // hành vi ĐÁNG NGỜ NHẤT (paste/chuyển tab rồi bỏ ngang). Additive: FE/CSV cũ bỏ qua field này, không vỡ.
        // Không xếp hạng/không điểm (chưa chấm) — chỉ danh tính + cờ để HR đánh giá (D13: cờ = gợi ý, không auto-hủy).
        public List<UnscoredFlaggedRow> UnscoredFlagged { get; set; } = new();
    }

    public class CampaignResultRow
    {
        // Hạng dẫn xuất lúc đọc (doc §campaign_rankings: KHÔNG lưu cột rank). Đồng điểm → cùng rank (1,1,3).
        public int Rank { get; set; }
        public Guid CandidateId { get; set; }
        // F5 — danh tính người-đọc-được (snapshot `campaign_membership`, fallback `cv_submission`).
        // Nullable: membership đường-1 lịch sử không có nguồn backfill → HR thấy ô trống, KHÔNG phải tên đoán.
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public Guid SessionId { get; set; }
        public decimal TotalScore { get; set; }
        // "Pass"/"Fail" so ngưỡng; null khi ngưỡng chưa đặt (HR quyết tay).
        public string? Result { get; set; }
        public DateTime ScoredAt { get; set; }

        // CAMP-18 — thước đo đã chấm dòng này. null = KHÔNG BIẾT (buổi chấm trước khi có nhãn);
        // FE hiện chip "?" và KHÔNG BAO GIỜ vẽ thành v1 (BK23).
        public int? RubricVersion { get; set; }

        // SCP1 · B8 / HĐ-5 — chính sách chấm đã áp cho dòng này. null = công thức mặc định / trước
        // SCP1 ⇒ FE không hiện nhãn. `ScoreFallback` = true ⇒ biểu thức lỗi lúc chạy, điểm này là
        // công thức weighted mặc định — PHẢI hiện ra UI (không thì lại là thứ hỏng im lặng).
        public int? PolicyVersion { get; set; }
        public string? PolicyName { get; set; }
        public bool ScoreFallback { get; set; }

        // RNK1 · HĐ-3 — số câu (từ campaign_rankings.scoring_inputs; snapshot trước RNK1 thiếu khoá
        // ⇒ null). `Answered`/`TotalQuestions` = mọi câu buổi; `SeedAnswered`/`SeedTotal` = riêng câu
        // GỐC (kind=Seed) = mẫu số của luật câu bỏ trống (HĐ-2). `SkipPenalty` = buổi này có áp luật.
        public int? Answered { get; set; }
        public int? TotalQuestions { get; set; }
        public int? SeedAnswered { get; set; }
        public int? SeedTotal { get; set; }
        public bool? SkipPenalty { get; set; }

        // RNK1 · HĐ-3 — sàng CV (cv_submission; null = mời bằng email KHÔNG có CV). Score do
        // CampaignService TÍNH (CAMP-14), risk là CỜ ĐỨNG CẠNH điểm — KHÔNG gộp vào TotalScore.
        // `CvScreeningVersion`: 1/null = thang cũ (LLM phán) · 2 = tính từ bằng chứng.
        public int? CvMatchScore { get; set; }
        public string? CvVerificationRisk { get; set; }   // "Low" | "Medium" | "High"
        public int? CvScreeningVersion { get; set; }

        // RNK1 · HĐ-5 — tiêu chí có pct < minPct (điểm sàn) ⇒ kết luận Fail. B4 điền; B2 để RỖNG.
        public List<BelowCutoffItem> BelowCutoff { get; set; } = new();

        // E11b — HR chốt điểm cuối. Effective (đã áp override) = TotalScore/Result ở trên ĐÃ tính theo override;
        // các cột dưới lộ override thô để FE hiện badge "HR chỉnh" + điểm AI gốc.
        public decimal AiScore { get; set; }          // điểm AI gốc (snapshot, không đổi khi override)
        public decimal? OverrideScore { get; set; }
        public string? OverrideResult { get; set; }
        public string? OverrideNote { get; set; }
        public DateTime? OverriddenAt { get; set; }

        // SEC-4: cờ chống gian lận gom theo buổi (signal_type → count). Additive — mặc định rỗng
        // (campaign không bật anti-cheat / không có cờ → []), KHÔNG phá client cũ. HR đánh giá lại (không auto-hủy).
        public List<FlagDto> Flags { get; set; } = new();
    }

    // RNK1 · HĐ-5 — 1 tiêu chí rớt điểm sàn. `CriterionId` null khi khớp theo TÊN (snapshot cũ không
    // có id) ⇒ `MatchedBy = "name"`; có id ⇒ `"id"`.
    public class BelowCutoffItem
    {
        public Guid? CriterionId { get; set; }
        public string Name { get; set; } = null!;
        public decimal Pct { get; set; }
        public decimal MinPct { get; set; }
        public string MatchedBy { get; set; } = null!;   // "id" | "name"
    }

    // SEC-4: 1 loại cờ đã gom cho HR — Type=signal_type, Count=số lần trong buổi, Note=1 ghi chú đại diện (nếu có).
    public class FlagDto
    {
        public string Type { get; set; } = null!;
        public int Count { get; set; }
        public string? Note { get; set; }

        // AC1 — mốc thời gian của NHÓM này (min/max `session_flags.detected_at`). Count một mình không
        // phân biệt được "5 lần chuyển tab trong 10 giây" (một cú alt-tab) với "5 lần rải đều 40 phút"
        // (hành vi có hệ thống) — hai thứ HR xử lý khác hẳn nhau. Additive, ĐẶT Ở CUỐI: client cũ bỏ qua.
        // Nullable để DTO không nói dối khi được dựng ở chỗ không có dữ liệu thời gian; đường đọc
        // GroupFlagsBySession luôn điền cả hai. Drill-down từng giây vẫn ở SessionFlagTimelineResponse.
        public DateTime? FirstAt { get; set; }
        public DateTime? LastAt { get; set; }

        // MON1-B4 — nguồn ghi cờ (B1 `session_flags.source`). "Client" = ứng viên tự báo qua trình duyệt
        // ⇒ CHẶN được; "Server" = server suy ra từ face_images ⇒ ứng viên KHÔNG chặn được. NOT null,
        // mặc định "Client" (khớp cờ cũ + cờ client hiện tại). Nhóm cờ TÁCH theo (type, source): một
        // signal_type có cả hai nguồn ⇒ HAI FlagDto — gộp làm mất chính thông tin đang muốn nói.
        // ĐẶT Ở CUỐI (sau firstAt/lastAt) theo quy ước additive: client cũ đọc tuần tự không lệch.
        public string Source { get; set; } = "Client";   // = FlagSource.Client

        // MON1-B4 — tóm tắt cờ cho CSV/PDF (F16: một chiến dịch → hai bản xuất KHÔNG được lệch nhau).
        // MỘT hàm duy nhất cho cả 4 chỗ (CSV results/unscored + PDF results/unscored) — fork logic là
        // đường để hai bản trôi xa nhau mà không test nào đỏ. "type(source):count" ngăn bởi "; ".
        public static string SummarizeForExport(IEnumerable<FlagDto> flags)
            => string.Join("; ", flags.Select(f => $"{f.Type}({f.Source}):{f.Count}"));
    }

    // R7 — 1 ứng viên có cờ mà CHƯA Scored: chỉ danh tính (F5) + cờ (không rank/điểm vì chưa chấm).
    public class UnscoredFlaggedRow
    {
        public Guid CandidateId { get; set; }
        public Guid SessionId { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public List<FlagDto> Flags { get; set; } = new();

        // RNK1 · HĐ-3 — điểm sàng CV vẫn xem được kể cả khi buổi phỏng vấn bỏ ngang (cv_submission,
        // null = mời bằng email không có CV). Risk = cờ đứng cạnh, KHÔNG vào điểm.
        public int? CvMatchScore { get; set; }
        public string? CvVerificationRisk { get; set; }
    }

    // E11b — HR chốt/sửa điểm cuối. Note bắt buộc (ghi audit). Score/Result đều null = CLEAR override (về AI).
    public class OverrideResultRequest
    {
        public decimal? Score { get; set; }
        public string? Result { get; set; }   // "Pass" | "Fail" | null
        public string Note { get; set; } = null!;
    }

    // E6 — kết quả xuất file (CSV/PDF) cho `GET /campaign/{id}/results/export`.
    // Controller trả `File(Content, ContentType, FileName)` (bám pattern DownloadCampaignFiles).
    public class CampaignResultExport
    {
        public byte[] Content { get; set; } = System.Array.Empty<byte>();
        public string ContentType { get; set; } = "text/csv";
        public string FileName { get; set; } = "results.csv";
    }

    // AI4 — chi tiết transcript 1 buổi phỏng vấn cho HR (`GET /campaign/{id}/results/{sessionId}/transcript`).
    // Đọc XUYÊN SERVICE từ Interview (`/internal/sessions/{sessionId}/answers`, GEN-2 ref lỏng) — HR đối chiếu
    // điểm ranking (E5) với transcript thật + dẫn chứng AI (E11) + cờ needs_review (E10). KHÔNG lưu Campaign DB.
    public class SessionTranscriptResponse
    {
        public System.Guid SessionId { get; set; }
        public List<TranscriptQuestion> Questions { get; set; } = new();
    }

    // 1 câu hỏi + câu trả lời (transcript) + điểm/nhận xét AI per-criterion. answer trống (chưa nộp/Skipped)
    // → Transcript null, Scores rỗng, NeedsReview false.
    public class TranscriptQuestion
    {
        public System.Guid QuestionId { get; set; }
        public int OrderNo { get; set; }
        public string Content { get; set; } = null!;
        public string? Transcript { get; set; }
        // E10 — self-consistency spread vượt ngưỡng → HR nên soi lại (điểm AI = gợi ý, D13).
        public bool NeedsReview { get; set; }
        public List<TranscriptCriterionScore> Scores { get; set; } = new();
    }

    // Điểm + nhận xét (reasoning, E11 trích dẫn transcript) của 1 tiêu chí. CriterionId = ref lỏng
    // (rubric_criteria phía Interview).
    // CriterionName/MaxScore do Interview trả kèm: id này KHÁC `campaign_criteria.id` (Interview mint
    // `Guid.NewGuid()` lúc materialize) nên Campaign/FE KHÔNG tra ngược tên được — thiếu nó thì màn
    // transcript của HR hiện "Tiêu chí a3f81b2c — 3" thay vì "Giao tiếp — 3/5". Nullable vì buổi chấm
    // trước 2026-07-18 không có 2 field này.
    public class TranscriptCriterionScore
    {
        public System.Guid CriterionId { get; set; }
        public string? CriterionName { get; set; }
        public decimal Score { get; set; }
        public int? MaxScore { get; set; }
        public string? Reasoning { get; set; }
    }

    // Log cờ chống gian lận THEO GIÂY cho 1 buổi (`GET /campaign/{id}/results/{sessionId}/flags`).
    // Khác `CampaignResultRow.Flags`/`UnscoredFlaggedRow.Flags` (SEC-4, GOM theo signal_type→count):
    // đây là DÒNG THỜI GIAN từng sự kiện — `session_flags.DetectedAt` vốn đã có sẵn theo giây nhưng bị
    // gộp mất trước khi tới HR (đếm gộp đúng chỗ dùng cho bảng/CSV/PDF, không phù hợp khi HR cần soi
    // "lúc mấy giờ, mấy lần, cách nhau bao lâu"). Additive — KHÔNG đổi shape `CampaignResultsResponse`.
    // KHÔNG đòi ranking row tồn tại (khác transcript AI4): phải xem được cả session CHƯA Scored/bỏ ngang
    // (R7 — nhóm đáng ngờ nhất). Không có cờ nào → Events=[] (không 404).
    public class SessionFlagTimelineResponse
    {
        public System.Guid SessionId { get; set; }
        public System.Guid CandidateId { get; set; }
        public List<SessionFlagEvent> Events { get; set; } = new();
    }

    // 1 dòng session_flags = 1 sự kiện phát hiện, giữ nguyên mốc thời gian gốc (UTC).
    public class SessionFlagEvent
    {
        public string SignalType { get; set; } = null!;
        public System.DateTime DetectedAt { get; set; }
        public string? Note { get; set; }
    }
}
