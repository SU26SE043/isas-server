using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    /// <summary>D2 — gọi InterviewService /internal/sessions/campaign (create-or-get session B2B, máy-máy).</summary>
    public interface ICampaignSessionClient
    {
        // BK18 — expiresAt = campaigns.expires_at; Interview set session.Deadline (I2) → sweeper auto-submit/
        // abandon quá hạn. null (B2C hoặc campaign không đặt hạn) = không hard-deadline.
        // BK14 — orgId = chủ ví credit (Campaign.OrgId) → Interview reserve owner=Org (PAY-6).
        // Ví org hết credit → InsufficientOrgCreditException (402), KHÔNG tạo session.
        // INT-17 — adaptiveEnabled/maxFollowUps/maxQuestions = toggle + trần HR đặt trên campaign.
        // Interview đóng dấu lên session lúc tạo (null → tắt / mặc định).
        // INT-17b — maxDeepPerQuestion: null/0 giữ hành vi cũ (seed = toàn bộ campaign questions, câu
        // thích ứng chỉ thêm ở ĐUÔI sau khi trả lời hết seed); > 0 thì MỖI câu campaign mọc chuỗi đào
        // sâu XEN KẼ ngay sau nó (vẫn công bằng: cùng bộ câu gốc, cùng trần độ sâu cho mọi ứng viên).
        // PR160 — `seniority` (mức kinh nghiệm HR chọn cho campaign) đứng TRƯỚC `ct`: CA1068 đòi
        // CancellationToken là tham số CUỐI. Tham số nằm sau `ct` vừa trái quy ước .NET, vừa dễ khiến
        // caller positional truyền nhầm hai thứ cho nhau khi đọc lướt.
        Task<CampaignSessionResult> CreateOrGetSessionAsync(
            Guid candidateId, Guid campaignId, Guid orgId, string jobCategory,
            IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria,
            DateTime? expiresAt = null,
            bool? adaptiveEnabled = null, int? maxFollowUps = null, int? maxQuestions = null,
            int? maxDeepPerQuestion = null,
            string seniority = "Junior",
            // CAMP-18 — định danh bộ thước đo. Interview CHỈ CHÉP số này xuống buổi thi (không tự
            // đánh số): materialize là lazy nên bên đó không thể suy ra đúng số HR đang nhìn thấy.
            int rubricVersion = 1,
            IReadOnlyList<SessionQuestionInput>? questionDetails = null,
            // SCP1 · B5 — hợp đồng chấm điểm (chính sách biểu thức) đang áp cho campaign. null =
            // campaign chưa áp chính sách nào ⇒ buổi thi dùng công thức weighted mặc định.
            CampaignScoringPolicyInput? scoringPolicy = null,
            // RNK1 · HĐ-2 / CAMP-21 — campaigns.skip_penalty (server-owned). Interview ghim
            // practice_sessions.skip_penalty; true ⇒ điểm tổng = clamp(expr × seed_completeness, 0, 100).
            // Default true = campaign tạo từ bản RNK1 trở đi (caller thực luôn truyền campaign.SkipPenalty).
            bool skipPenalty = true,
            CancellationToken ct = default);
        // Overload đầy đủ: KHÔNG đặt default cho `language`/`seniority`/`ct` — caller duy nhất
        // (ParticipationService) truyền đủ, và để trống default thì hai overload không thể nhập nhằng.
        Task<CampaignSessionResult> CreateOrGetSessionAsync(Guid candidateId, Guid campaignId, Guid orgId, string jobCategory, IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria, DateTime? expiresAt, bool? adaptiveEnabled, int? maxFollowUps, int? maxQuestions, int? maxDeepPerQuestion, string language, string seniority, int rubricVersion, IReadOnlyList<SessionQuestionInput>? questionDetails, CampaignScoringPolicyInput? scoringPolicy, bool skipPenalty, CancellationToken ct);

        // AI4 — HR đọc transcript + nhận xét AI per-criterion + cờ needs_review của 1 buổi (đối chiếu điểm
        // ranking). Gọi Interview GET /internal/sessions/{sessionId}/answers (máy-máy, X-Internal-Token).
        // Lỗi hạ tầng / non-success → DownstreamServiceException (502).
        Task<SessionTranscriptResponse> GetSessionTranscriptAsync(
            Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// CAMP-20 — đọc BỘ CHUẨN B2C (admin soạn) để Employer chép về campaign.
        /// <c>GET /internal/rubrics/b2c?jobCategory=&amp;language=</c> (máy-máy, X-Internal-Token).
        ///
        /// <para>Đặt ở ĐÂY chứ không dựng client thứ hai: đích đến y hệt (InterviewService), nên client
        /// riêng nghĩa là hai chỗ cấu hình BaseUrl/token/timeout — lệch một chỗ thì hỏng một nửa số
        /// đường gọi mà nửa kia vẫn chạy, tức triệu chứng khó truy nhất.</para>
        ///
        /// <para><b>Không có <c>id</c> và không có <c>scoringScope</c> trong hợp đồng.</b> Id là của
        /// Interview, vô nghĩa với Campaign (đường ghi bên này replace-all mint id mới). ScoringScope
        /// thì Campaign KHÔNG có cột tương ứng và đường chấm B2B không đọc — mang về chỉ để lưu là dựng
        /// một cột nói dối.</para>
        ///
        /// <para><c>levels</c> RỖNG = <b>chưa khai mốc</b> (admin chưa soạn), KHÔNG phải lỗi — Interview
        /// rơi về dải mặc định như trước CAMP-16.</para>
        ///
        /// <para>Ném <see cref="DownstreamServiceException"/> cho MỌI thất bại, gồm cả 404 "chưa có bộ
        /// chuẩn cho tổ hợp này" (controller map 502). Cố ý KHÔNG fallback bịa ra một bộ tiêu chí:
        /// HR sẽ tin đó là bộ chuẩn do admin soạn rồi phát cho ứng viên thật.</para>
        /// </summary>
        Task<B2CRubricResponse> GetB2CRubricAsync(
            string jobCategory, string language, CancellationToken ct = default);
    }

    /// <summary>
    /// CAMP-20 — bộ chuẩn B2C nhận từ Interview. <c>Version</c> là số của bộ đang active bên đó, mang
    /// về CHỈ để ghi audit/hiển thị "chép từ bản mấy" — nó KHÔNG phải <c>campaigns.rubric_version</c>
    /// (hai trục đánh số khác nhau: một do Interview cấp cho bộ chuẩn, một do Campaign cấp cho thước
    /// đo của chiến dịch).
    /// </summary>
    public record B2CRubricResponse(
        string JobCategory, string Language, int Version, IReadOnlyList<B2CRubricCriterion> Criteria);

    public record B2CRubricCriterion(
        string Name, string? Description, decimal Weight, int MaxScore,
        IReadOnlyList<B2CRubricLevel> Levels);

    public record B2CRubricLevel(int Score, string Descriptor);

    /// <summary>
    /// Một tiêu chí chấm gửi sang Interview. <c>Levels</c> là init-only có mặc định RỖNG (không phải
    /// tham số positional thứ 5) để mọi call site 4-tham-số đang có vẫn biên dịch và vẫn mang đúng
    /// nghĩa "chưa khai mốc" — Interview rơi về dải mặc định như trước CAMP-16.
    /// </summary>
    public record SessionCriterionInput(string Name, string? Description, decimal Weight, int MaxScore)
    {
        public IReadOnlyList<SessionCriterionLevelInput> Levels { get; init; }
            = Array.Empty<SessionCriterionLevelInput>();

        /// <summary>RNK1 · HĐ-5 — <c>campaign_criteria.id</c>. Interview ghi vào
        /// <c>rubric_criteria.source_criterion_id</c> (ref lỏng, không FK xuyên service) để snapshot
        /// chấm khớp về đúng tiêu chí khi tính điểm sàn read-time. Khoá JSON trên dây: <c>criterionId</c>.
        /// Init-only có mặc định null ⇒ call site cũ không phải sửa.</summary>
        public Guid? CriterionId { get; init; }
    }

    /// <summary>Một mốc điểm (E9 hard-anchor) — map 1-1 sang <c>rubric_levels</c> phía Interview.</summary>
    public record SessionCriterionLevelInput(int Score, string Descriptor);

    /// <summary>
    /// SCP1 · B5 — hợp đồng chấm điểm (chính sách biểu thức) của campaign, gửi sang Interview để ghim
    /// vào <c>practice_sessions</c>. Ghim CẢ biểu thức: Interview không đọc được bảng
    /// <c>scoring_policies</c> của Campaign lúc chấm/preview (DB-per-service). Chỉ tồn tại khi campaign
    /// ĐÃ áp một chính sách (<c>campaigns.interview_policy_version != null</c>); null = dùng công thức
    /// weighted mặc định.
    /// </summary>
    public record CampaignScoringPolicyInput(int Version, string Expression, int? PassScorePct, string EngineVersion);

    /// <summary>
    /// Một câu campaign kèm đáp án mẫu HR soạn (null = chưa soạn).
    ///
    /// <para>Gửi SONG SONG với <c>questions</c> (danh sách chuỗi) chứ không thay thế nó: hai service
    /// deploy không nguyên tử, nên trong cửa sổ giữa hai lần khởi động phải có bản Campaign mới nói
    /// chuyện được với bản Interview cũ và ngược lại. Interview ưu tiên danh sách này, vắng thì rơi về
    /// <c>questions</c>, và BỎ QUA nếu số lượng lệch (ghép theo chỉ số khi lệch sẽ gán đáp án của câu
    /// này cho câu kia — chấm sai mà không lỗi nào nổ).</para>
    /// </summary>
    public record SessionQuestionInput(string Text, string? SampleAnswer);

    public record CampaignSessionResult(Guid SessionId, IReadOnlyList<SessionQuestion> Questions);

    public record SessionQuestion(Guid Id, int OrderNo, string Content, int TimeLimitSec);
}
