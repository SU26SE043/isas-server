namespace Isas.CampaignService.DTOs
{
    // ── C14 — Callback worker sàng CV → CampaignService (X-Internal-Token) ──────────────
    // Shape khớp ai.md §Pipeline sàng CV: worker gọi cùng `analyze_cv`, callback về Campaign.
    // candidateId lấy từ ROUTE (không nằm trong body).

    /// <summary>cv-result — kết quả AI chấm khớp 1 CV theo tiêu chí campaign.</summary>
    public class CvResultCallbackRequest
    {
        /// <summary>BK28 — họ tên rút từ CV. <c>null</c> = CV không có tên rõ ràng (hợp lệ) ⇒
        /// <see cref="Services.CvScreeningService.SaveCvResultAsync"/> giữ nguyên giá trị đang có,
        /// KHÔNG ghi đè tên HR đã nhập tay qua PATCH.</summary>
        public string? FullName { get; set; }
        public List<string>? Skills { get; set; }
        public decimal? YearsExperience { get; set; }
        public List<string>? Education { get; set; }   // chấp nhận nhưng KHÔNG lưu (C13 schema không có cột)

        // ── HR technical screener (bước 2-4) ──────────────────────────────────
        /// <summary>2-3 câu: ứng viên hợp/không hợp ở đâu.</summary>
        public string? FitSummary { get; set; }
        /// <summary>Đánh giá theo TỪNG nhu cầu của campaign; needId lạ/trùng → bỏ (AI-3).</summary>
        public List<NeedAssessmentItem> Assessments { get; set; } = new();
        /// <summary>Điểm cộng ngoài bộ nhu cầu (production, CI/CD, cloud, mentoring…).</summary>
        public List<string>? BonusSignals { get; set; }
        /// <summary>Low | Medium | High — cờ cho HR, KHÔNG nhập vào điểm.</summary>
        public string? VerificationRisk { get; set; }
        /// <summary>Tối đa 3 câu cần hỏi để xác minh (cắt lại phía Campaign).</summary>
        public List<string>? VerifyQuestions { get; set; }

        // 🔴 KHÔNG có điểm tổng trong hợp đồng này, và đó là chủ đích: `CvScreeningService` tính
        // từ `Assessments`. Nhận một con số do AI phán là mở lại đúng đường đã bịt — trên prod
        // bốn CV bằng chứng giống hệt nhau từng nhận 70/70/55/55.
    }

    /// <summary>Đánh giá CV theo 1 nhu cầu công việc của campaign.</summary>
    public class NeedAssessmentItem
    {
        public string? NeedId { get; set; }   // phải khớp campaigns.job_needs[].needId (id AI bịa → bỏ)
        public string? Area { get; set; }
        public string? Level { get; set; }    // Strong | Partial | Weak (mức lạ → Weak)
        public string? Evidence { get; set; } // TRÍCH từ CV; rỗng → "Không thấy bằng chứng" + hạ Weak
    }

    /// <summary>cv-failed — worker báo lỗi vĩnh viễn khi phân tích 1 CV.</summary>
    public class CvFailedCallbackRequest
    {
        public string? Reason { get; set; }
    }

    // ── C14 — Shortlist (đọc kết quả sàng cho HR) ──────────────────────────────────────

    /// <summary>1 dòng shortlist (GET danh sách). <c>OverallMatchScore</c> null tới khi Analyzed.</summary>
    public class CandidateListItem
    {
        public Guid Id { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string Status { get; set; } = null!;
        /// <summary>Độ khớp công việc 0–100, TÍNH từ mức bằng chứng (xem <c>ScreeningVersion</c>).</summary>
        public int? OverallMatchScore { get; set; }
        public List<string>? Skills { get; set; }
        /// <summary>
        /// Low | Medium | High — cờ đứng CẠNH điểm, không nằm trong điểm. `High` = CV liệt kê rất
        /// nhiều kỹ năng mà không dự án nào chống lưng ⇒ điểm cao vẫn cần soi kỹ.
        /// </summary>
        public string? VerificationRisk { get; set; }
        /// <summary>1 = điểm cũ do LLM phán trên rubric phỏng vấn · 2 = tính từ bằng chứng. Hai
        /// thang KHÔNG so sánh được — có dấu để không bị trộn trong im lặng (tiền lệ BK23).</summary>
        public int? ScreeningVersion { get; set; }
    }

    /// <summary>
    /// Chi tiết 1 ứng viên (GET đơn) — kết quả HR technical screener + KEY CV gốc.
    /// </summary>
    public class CandidateDetailResponse
    {
        public Guid Id { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string Status { get; set; } = null!;
        public int? OverallMatchScore { get; set; }
        public List<string>? Skills { get; set; }
        public decimal? YearsExperience { get; set; }
        public string? Summary { get; set; }
        public string? RejectReason { get; set; }   // lý do Rejected (hard-filter) hoặc AnalysisFailed (AI lỗi)
        public string? CvFileUrl { get; set; }       // S3 KEY (GEN-5 — không full URL)

        // ── HR technical screener ─────────────────────────────────────────────
        public int? ScreeningVersion { get; set; }
        public string? FitSummary { get; set; }
        /// <summary>Nhu cầu ứng viên ĐÁP ỨNG (Strong/Partial), kèm trích dẫn từ CV.</summary>
        public List<NeedAssessmentItem> Strengths { get; set; } = new();
        /// <summary>Nhu cầu CHƯA thấy bằng chứng (Weak) — chính là việc cần hỏi ở vòng phỏng vấn.</summary>
        public List<NeedAssessmentItem> Gaps { get; set; } = new();
        public List<string> BonusSignals { get; set; } = new();
        public string? VerificationRisk { get; set; }
        /// <summary>
        /// Tối đa 3 câu nên hỏi để xác minh. ⚠ CHỈ hiển thị cho HR — KHÔNG tự ghi vào
        /// <c>campaign_questions</c>: bộ câu campaign là bộ CHUNG cho mọi ứng viên, đó là nền tảng
        /// khiến bảng xếp hạng CAMP-10 so sánh được; nhét câu riêng theo từng CV vào đó là phá nó.
        /// </summary>
        public List<string> VerifyQuestions { get; set; } = new();
    }

    /// <summary>PATCH — HR bổ sung/sửa email/fullName khi CV không tách được (chỉ trường gửi lên).</summary>
    public class PatchCandidateRequest
    {
        public string? Email { get; set; }
        public string? FullName { get; set; }
    }
}
