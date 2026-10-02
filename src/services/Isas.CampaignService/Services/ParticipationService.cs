using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Pagination;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// D2 — orchestrator luồng ứng viên: xem lời mời → tham gia (join) → membership → my-campaigns →
    /// bắt đầu phỏng vấn. Provision Candidate qua Auth (INT-13/D8), tạo session qua Interview (I1).
    /// KHÔNG FK xuyên service (candidateId/sessionId = Guid lỏng).
    ///
    /// DB16 — membership sống ở bảng riêng <see cref="CampaignMembership"/> (tách khỏi bảng God
    /// cũ <c>campaign_candidates</c> = nay <see cref="CvSubmission"/>). "Đã join" = tồn tại row
    /// membership; SessionId/InterviewStatus/ReferenceImageKey đọc/ghi TRÊN membership.
    /// </summary>
    public class ParticipationService : IParticipationService
    {
        private readonly CampaignDbContext _db;
        private readonly IAuthProvisionClient _authClient;
        private readonly ICampaignSessionClient _sessionClient;
        private readonly IOrgNameResolver? _orgNameResolver;
        private readonly ILogger<ParticipationService> _logger;

        public ParticipationService(
            CampaignDbContext db,
            IAuthProvisionClient authClient,
            ICampaignSessionClient sessionClient,
            ILogger<ParticipationService> logger,
            IOrgNameResolver? orgNameResolver = null)
        {
            _db = db;
            _authClient = authClient;
            _sessionClient = sessionClient;
            _orgNameResolver = orgNameResolver;   // CMP1-B1 — null ⇒ orgName không resolve (giữ null, không ném)
            _logger = logger;
        }

        // ── GET /invitations/{token} — metadata (KHÔNG side-effect) ──────────────────
        public async Task<InvitationMetadataResponse> GetInvitationMetadataAsync(string token, CancellationToken ct = default)
        {
            var tokenHash = HashOrThrow(token);   // DB23 — tra bằng hash (DB không giữ token thô)
            var inv = await _db.CampaignInvitations
                .AsNoTracking()
                .Include(i => i.Campaign).ThenInclude(c => c.Criteria)
                .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct)
                ?? throw new KeyNotFoundException("Lời mời không tồn tại.");

            ValidateInvitationUsable(inv);

            return new InvitationMetadataResponse
            {
                CampaignId = inv.CampaignId,
                Title = inv.Campaign.Title,
                // CMP1-B1 — resolve tên org qua Auth. Fail-soft: bọc thêm try/catch ở đây (ngoài
                // fail-soft trong resolver) để một resolver tương lai regress cũng không hạ được
                // đường đọc lời mời của ứng viên ẩn danh.
                OrgName = await ResolveOrgNameSafeAsync(inv.Campaign.OrgId, ct),
                JobTitle = inv.Campaign.Domain,
                Description = inv.Campaign.JDText,
                StartsAt = inv.Campaign.StartsAt,        // CMP1-B1 — giờ MỞ phỏng vấn (≠ Deadline)
                Deadline = inv.Campaign.ExpiresAt,       // hạn LỜI MỜI — KHÔNG đổi nghĩa
                Criteria = inv.Campaign.Criteria.OrderBy(c => c.OrderNo).Select(MapCriterion).ToList()
            };
        }

        private async Task<string?> ResolveOrgNameSafeAsync(Guid orgId, CancellationToken ct)
        {
            if (_orgNameResolver is null) return null;
            try
            {
                return await _orgNameResolver.ResolveOrgNameAsync(orgId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resolve tên org {OrgId} ném — orgName = null.", orgId);
                return null;
            }
        }

        // ── POST /invitations/{token}/join — tham gia campaign ───────────────────────
        public async Task<JoinCampaignResponse> JoinCampaignAsync(string token, string? callerEmail, CancellationToken ct = default)
        {
            var tokenHash = HashOrThrow(token);   // DB23 — tra bằng hash (DB không giữ token thô)
            var inv = await _db.CampaignInvitations
                .Include(i => i.Campaign)
                .Include(i => i.CvSubmission)   // F5 — nguồn full_name cho đường-2 (shortlist); đường-1 = null
                .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct)
                ?? throw new KeyNotFoundException("Lời mời không tồn tại.");

            ValidateInvitationUsable(inv);

            // BK26: token mời chỉ được redeem bởi Candidate đang đăng nhập bằng đúng email được mời.
            // Guard này phải đứng trước provision/membership để caller sai không tạo side-effect nào.
            if (string.IsNullOrWhiteSpace(callerEmail)
                || !string.Equals(callerEmail.Trim(), inv.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Email đăng nhập không khớp với email được mời.");

            // Provision account Candidate nhẹ theo email của lời mời (idempotent bên Auth).
            var provisioned = await _authClient.ProvisionCandidateAsync(inv.Email, null, ct);
            var candidateId = provisioned.CandidateId;
            var now = DateTime.UtcNow;

            var membership = await ResolveMembershipAsync(inv, candidateId, ct);
            if (membership is null)
            {
                membership = new CampaignMembership
                {
                    Id = Guid.NewGuid(),
                    CampaignId = inv.CampaignId,
                    CandidateId = candidateId,
                    // Đường 2 (shortlist) — link về CV đã sàng; đường 1 (mời-thẳng email) = null.
                    CvSubmissionId = inv.CampaignCandidateId,
                    Status = MembershipStatus.Joined,
                    // (InvitationId set trong ApplyInvitationLink bên dưới — CHUNG cho cả 2 nhánh.)
                    JoinedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                ApplyInvitationLink(membership, inv);
                _db.CampaignMemberships.Add(membership);
            }
            else
            {
                // Idempotent: gắn candidate + đánh Joined (không hạ cấp / không tạo row thứ 2).
                membership.CandidateId = candidateId;
                if (membership.Status != MembershipStatus.Joined)
                    membership.Status = MembershipStatus.Joined;
                membership.JoinedAt ??= now;
                membership.UpdatedAt = now;
                // F5/FX1 — PHẢI chạy ở CẢ nhánh idempotent: membership tồn tại từ trước F5 (hoặc join lại
                // sau khi reissue lời mời) vẫn cần điền danh tính + gắn lời mời, không thì HR xuất CSV ra
                // ô trống vĩnh viễn và lời mời mới không bao giờ hiện "Joined".
                ApplyInvitationLink(membership, inv);
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "D2 join: candidate {CandidateId} tham gia campaign {CampaignId} (membership {MembershipId})",
                candidateId, inv.CampaignId, membership.Id);

            return new JoinCampaignResponse
            {
                AccessToken = provisioned.AccessToken,
                CampaignId = inv.CampaignId,
                CandidateId = candidateId,
                MembershipStatus = membership.Status.ToString()
            };
        }

        // ── GET /my-campaigns ────────────────────────────────────────────────────────
        // Keyset-paged (DB8) theo (CreatedAt DESC, Id DESC) của membership. Trước đây sắp theo JoinedAt
        // DESC — cột NULLABLE nên không dùng làm khoá keyset được (NULL trong predicate cho UNKNOWN, và
        // thứ tự NULL khác nhau giữa Postgres/SQLite). Membership CreatedAt = thời điểm join nên thứ tự
        // hiển thị thực tế không đổi; ThenBy(Id) là tie-break duy nhất-toàn-cục.
        public async Task<KeysetPage<MyCampaignItem>> GetMyCampaignsAsync(
            Guid candidateId, string? cursor, int? limit, CancellationToken ct = default)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            // Soft-delete campaign (D11) đã được loại Ở SQL bởi global query filter DB13 trên
            // CampaignMembership (`x.Campaign.DeletedAt == null`) → KHÔNG cần vị ngữ thủ công nào ở đây,
            // và quan trọng hơn: trang không bị campaign đã xoá chiếm chỗ rồi mới bị bỏ trong C#.
            var q = _db.CampaignMemberships
                .AsNoTracking()
                .Where(m => m.CandidateId == candidateId);

            if (cur is not null)
                q = q.Where(m => m.CreatedAt < cur.CreatedAt
                    || (m.CreatedAt == cur.CreatedAt && m.Id.CompareTo(cur.Id) < 0));

            var rows = await q
                .Include(m => m.Campaign)
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id)
                .Take(take)
                .ToListAsync(ct);

            var next = rows.Count == take
                ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
                : null;

            // Guard phòng thủ: query filter DB13 đã loại campaign đã xoá, nhưng DTO deref .Title ngay dưới.
            var items = rows.Where(m => m.Campaign is not null).Select(m => new MyCampaignItem
            {
                CampaignId = m.CampaignId,
                Title = m.Campaign.Title,
                Company = null,
                JobTitle = m.Campaign.Domain,
                Deadline = m.Campaign.ExpiresAt,
                MembershipStatus = m.Status.ToString(),
                InterviewStatus = MapCandidateInterviewStatus(m.InterviewStatus).ToString(),
                // ATT1 [C6]
                TimeLimitMinutes = m.Campaign.TimeLimitMinutes,
                MaxAttempts = m.Campaign.MaxAttempts,
                AttemptsUsed = m.AttemptCount,
                LastAttemptAbandoned = m.InterviewStatus == InterviewProgressStatus.Abandoned
            }).ToList();

            return new KeysetPage<MyCampaignItem>(items, next);
        }

        // ── GET /my-campaigns/{id} — chi tiết cho ứng viên đã join ────────────────────
        public async Task<CandidateCampaignDetailResponse> GetCandidateCampaignAsync(
            Guid candidateId, Guid campaignId, CancellationToken ct = default)
        {
            var membership = await _db.CampaignMemberships
                .AsNoTracking()
                .Include(m => m.Campaign).ThenInclude(c => c.Criteria)
                .FirstOrDefaultAsync(m => m.CampaignId == campaignId && m.CandidateId == candidateId, ct);

            if (membership is null || membership.Campaign is null)
                throw new KeyNotFoundException("Bạn không phải thành viên của chiến dịch này.");

            var interviewStatus = MapCandidateInterviewStatus(membership.InterviewStatus);

            return new CandidateCampaignDetailResponse
            {
                CampaignId = campaignId,
                Title = membership.Campaign.Title,
                JobTitle = membership.Campaign.Domain,
                Description = membership.Campaign.JDText,
                Deadline = membership.Campaign.ExpiresAt,
                Criteria = membership.Campaign.Criteria.OrderBy(c => c.OrderNo).Select(MapCriterion).ToList(),
                MembershipStatus = membership.Status.ToString(),
                InterviewStatus = interviewStatus.ToString(),
                SessionId = membership.SessionId,
                Started = membership.SessionId is not null || interviewStatus != InterviewProgressStatus.NotStarted,
                // ATT1 [C6]
                TimeLimitMinutes = membership.Campaign.TimeLimitMinutes,
                MaxAttempts = membership.Campaign.MaxAttempts,
                AttemptsUsed = membership.AttemptCount,
                LastAttemptAbandoned = membership.InterviewStatus == InterviewProgressStatus.Abandoned
            };
        }

        // ── POST /campaign/{id}/start — bắt đầu phỏng vấn (create-or-get session) ──────
        public async Task<StartInterviewResponse> StartInterviewAsync(
            Guid candidateId, Guid campaignId, CancellationToken ct = default)
        {
            var membership = await _db.CampaignMemberships
                .Include(m => m.Campaign).ThenInclude(c => c.Questions)
                .Include(m => m.Campaign).ThenInclude(c => c.Criteria)
                    // CAMP-16 — mốc điểm đi kèm tiêu chí sang Interview. CHỈ nạp ở đường Start; hai
                    // đường ứng viên ĐỌC (metadata lời mời, chi tiết campaign) cố ý KHÔNG nạp vì mốc
                    // là thước đo nội bộ (quyết định 7).
                    .ThenInclude(cr => cr.Levels)
                .FirstOrDefaultAsync(m => m.CampaignId == campaignId && m.CandidateId == candidateId, ct);

            // Chưa join (không có membership gắn candidate) → 403 (UnauthorizedAccessException).
            if (membership is null || membership.Campaign is null)
                throw new UnauthorizedAccessException("Bạn cần tham gia chiến dịch trước khi bắt đầu phỏng vấn.");

            var campaign = membership.Campaign;

            // Campaign còn cho phỏng vấn: Active + chưa hết hạn (→ 409 nếu không).
            if (campaign.Status != CampaignStatus.Active)
                throw new InvalidOperationException($"Chiến dịch không còn cho phỏng vấn (trạng thái {campaign.Status}).");
            if (campaign.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
                throw new InvalidOperationException("Chiến dịch đã hết hạn phỏng vấn.");
            // Đã hoàn thành → không cho làm lại (biên idempotency phía membership).
            if (membership.InterviewStatus == InterviewProgressStatus.Completed)
                throw new InvalidOperationException("Bạn đã hoàn thành phỏng vấn của chiến dịch này.");

            var now = DateTime.UtcNow;
            var isResume = membership.SessionId is not null
                && membership.InterviewStatus == InterviewProgressStatus.InProgress;

            // ATT1 R1 — membership "InProgress" chỉ là điều Campaign BIẾT LẦN CUỐI: sự kiện kết thúc buổi
            // (SessionScored / SessionAbandoned) đi qua outbox → broker nên có thể chưa tới (~15s, broker
            // chết thì lâu hơn). Tin mù vào đó thì guard lượt bị bỏ qua, Interview thấy buổi cũ đã đóng ⇒
            // tạo buổi MỚI + giữ credit ⇒ vượt trần lượt, và buổi mới rút đề lượt cũ. Nên khi tưởng là
            // "vào lại", HỎI trạng thái thật bên Interview. CHỈ hỏi ở nhánh này: Start lần đầu / lượt sau bỏ
            // ngang không thêm round-trip. Lỗi gọi ⇒ DownstreamServiceException (502) — fail-closed, không đoán.
            // ⚠ Còn cửa sổ đua CỰC HẸP giữa lúc hỏi và lúc create-or-get (buổi đóng đúng khoảng giữa) —
            // chấp nhận: khi đó vẫn đếm đúng buổi mới (isNewSession), chỉ có thể vượt trần một lượt.
            if (isResume)
            {
                var live = await _sessionClient.GetSessionStateAsync(membership.SessionId!.Value, ct);
                switch (ClassifyLiveSession(live))
                {
                    case LiveSessionVerdict.Submitted:
                        // Bài đã nộp/chấm bên Interview ⇒ đối xử như guard Completed ở trên (không làm lại
                        // bài đã nộp, kể cả khi còn lượt — guard Completed đứng trước guard lượt).
                        throw new InvalidOperationException("Bạn đã hoàn thành phỏng vấn của chiến dịch này.");
                    case LiveSessionVerdict.Ended:
                        // Buổi đã bỏ ngang / hỏng / không tồn tại ⇒ KHÔNG phải vào lại: guard lượt áp, còn
                        // lượt thì là lượt mới (attemptNo = count + 1, đề mới).
                        _logger.LogInformation(
                            "ATT1: membership {MembershipId} còn InProgress nhưng Interview báo session {SessionId} {Status} — coi là KHÔNG vào lại.",
                            membership.Id, membership.SessionId, live?.Exists == false ? "không tồn tại" : live?.Status);
                        isResume = false;
                        break;
                }
            }

            // ATT1 [C8] — hết lượt làm bài (và không phải đang làm dở) ⇒ 409 ATTEMPT_LIMIT_REACHED. Đứng
            // TRƯỚC khung giờ / sức chứa / gọi Interview: hết lượt mà vẫn đi tiếp là Interview giữ 1
            // credit tổ chức rồi mới 409 (PAY-5). Vào lại buổi đang dở KHÔNG bị chặn (không tạo lượt mới).
            if (!isResume && membership.AttemptCount >= campaign.MaxAttempts)
                throw new AttemptLimitReachedException(membership.AttemptCount, campaign.MaxAttempts);

            // ATT1 — lượt của buổi SẮP dùng: vào lại = lượt đang dở; buổi mới = lượt kế. Chỉ dùng để chọn
            // đề + trả về cho FE; bộ đếm CHỈ tăng sau khi Interview trả về session mới (xem dưới).
            var attemptNo = isResume ? membership.AttemptCount : membership.AttemptCount + 1;
            DateTime? interviewDeadline = campaign.ExpiresAt;

            // Resume đã có session idempotent, không chiếm thêm slot/capacity và không gọi lại reserve.
            if (!isResume)
            {
                // Resume tiếp session đã mở không được chặn nếu HR đổi StartsAt về tương lai.
                if (campaign.StartsAt is DateTime startsAt && startsAt > now)
                    throw new InvalidOperationException("Chiến dịch chưa đến thời gian bắt đầu phỏng vấn.");

                if (membership.SlotId is Guid slotId)
                {
                    var slot = await _db.CampaignSlots
                        .FirstOrDefaultAsync(s => s.Id == slotId && s.CampaignId == campaignId, ct);
                    if (slot is null || now < slot.StartsAt || now > slot.EndsAt)
                    {
                        if (slot is null)
                            throw new InvalidOperationException("Không tìm thấy khung giờ phỏng vấn đã được phân.");

                        var startsAtVn = ToVietnamTime(slot.StartsAt);
                        var endsAtVn = ToVietnamTime(slot.EndsAt);
                        throw new OutsideSlotWindowException(
                            slot.StartsAt, slot.EndsAt,
                            $"Bạn thi lúc {startsAtVn:HH:mm dd/MM} (giờ VN), kết thúc {endsAtVn:HH:mm}.");
                    }

                    interviewDeadline = MinDeadline(campaign.ExpiresAt, slot.EndsAt);
                }

                if (campaign.MaxConcurrentInterviews is int maxConcurrent)
                {
                    var inactiveSince = now.AddHours(-24);
                    var running = await _db.CampaignMemberships.CountAsync(m =>
                        m.CampaignId == campaignId
                        && m.InterviewStatus == InterviewProgressStatus.InProgress
                        && ((m.InterviewDeadlineAt != null && m.InterviewDeadlineAt > now)
                            || (m.InterviewDeadlineAt == null && m.UpdatedAt > inactiveSince)), ct);
                    if (running >= maxConcurrent)
                        throw new CampaignInterviewCapacityExceededException("Chiến dịch đang đạt giới hạn số phiên phỏng vấn đồng thời.");
                }
            }

            // NGÂN HÀNG ĐỀ — chọn đề CHO ỨNG VIÊN NÀY. `QuestionsPerSession = null` (mặc định, và là
            // trạng thái của mọi chiến dịch có trước tính năng này) ⇒ lấy trọn bộ theo đúng thứ tự HR
            // soạn, y như trước. Rút deterministic theo (campaignId, candidateId): buổi thi là
            // create-or-get, ứng viên vào lại phải nhận ĐÚNG đề cũ.
            // SC2 · W2 — mang theo nhãn tiêu chí để selector chia rổ theo TIÊU CHÍ CHÍNH; câu chưa gắn nhãn
            // (null) rơi về question_group y như trước. (Gửi nhãn sang Interview là việc của T3.)
            var pool = campaign.Questions
                .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id)
                .Select(q => new PoolQuestion(q.Id, q.QuestionText, q.SampleAnswer, q.IsRequired, q.QuestionGroup)
                {
                    TargetCriterionIds = q.TargetCriterionIds,
                })
                .ToList();
            if (pool.Count == 0)
                throw new InvalidOperationException("Chiến dịch chưa có câu hỏi.");

            // ATT1 — lượt ≥ 2 rút bộ câu gốc MỚI; lượt 1 rút ĐÚNG đề như trước ATT1 (hạt giống không đổi).
            var selected = QuestionPoolSelector.Select(
                pool, campaign.QuestionsPerSession, campaignId, candidateId,
                warning => _logger.LogWarning(
                    "Ngân hàng đề campaign {CampaignId}: {Warning}", campaignId, warning),
                attemptNo: attemptNo);

            var questions = selected.Select(q => q.Text).ToList();
            // Cùng danh sách, cùng thứ tự — Interview ghép theo chỉ số và BỎ QUA nếu số lượng lệch.
            // SC2 · W4 — nhãn lấy từ CHÍNH PoolQuestion đã rút (selector đã xáo/cắt), không tra lại pool gốc
            // theo chỉ số: lệch một vị trí là câu này mang nhãn câu kia — chấm sai mà không lỗi nào nổ.
            var questionDetails = selected
                .Select(q => new SessionQuestionInput(q.Text, q.SampleAnswer) { TargetCriterionIds = q.TargetCriterionIds })
                .ToList();

            // CAMP-16 — dựng qua ScoringCriteriaBuilder, KHÔNG map tay tại chỗ: đây là một trong hai
            // đường phải cho ra CÙNG một bộ thước đo với đường chấm thử của HR, và hai bản map tay sẽ
            // trôi xa nhau mà không có triệu chứng nào (cả hai vẫn ra điểm, chỉ khác thước).
            var criteria = ScoringCriteriaBuilder.Build(campaign.Criteria);
            if (criteria.Count == 0)
                throw new InvalidOperationException("Chiến dịch chưa có tiêu chí chấm.");

            var jobCategory = string.IsNullOrWhiteSpace(campaign.Domain) ? "BE" : campaign.Domain!;

            // SCP1 · B5 — HỢP ĐỒNG CHẤM ĐIỂM đang áp: đọc scoring_policies của CHÍNH campaign này
            // (Campaign sở hữu bảng), gửi CẢ biểu thức xuống buổi thi để Interview ghim — bên đó không
            // đọc được bảng này lúc chấm. `interview_policy_version` null = chưa áp chính sách nào ⇒
            // gửi null ⇒ Interview dùng công thức weighted mặc định (hành vi trước SCP1). Policy là
            // BẤT BIẾN (B2) nên chép xuống an toàn; đọc theo con trỏ, KHÔNG lọc is_active (không có
            // cột đó — RubricCriteriaLoader.cs:81-92: pin thì đọc theo version, không theo "đang bật").
            CampaignScoringPolicyInput? scoringPolicy = null;
            if (campaign.InterviewPolicyVersion is int policyVer)
            {
                var policy = await _db.ScoringPolicies.AsNoTracking().FirstOrDefaultAsync(
                    p => p.CampaignId == campaignId
                        && p.Kind == ScoringExpressionKind.Interview
                        && p.Version == policyVer, ct);
                if (policy is not null)
                    scoringPolicy = new CampaignScoringPolicyInput(
                        policy.Version, policy.Expression, policy.PassScorePct, policy.EngineVersion);
            }

            // Create-or-get session (Interview dedup theo candidate+campaign) → bấm nhiều lần vẫn ra CÙNG session.
            // Gửi deadline hiệu lực (min campaign expiry và slot) để Interview sweeper tự kết thúc đúng hạn.
            var session = campaign.Language == "vi"
                ? await _sessionClient.CreateOrGetSessionAsync(candidateId, campaignId, campaign.OrgId, jobCategory, questions, criteria, interviewDeadline,
                    campaign.AdaptiveEnabled, campaign.MaxFollowUps, campaign.MaxQuestions, campaign.MaxDeepPerQuestion, campaign.Seniority, campaign.RubricVersion, questionDetails, scoringPolicy, campaign.SkipPenalty, campaign.TimeLimitMinutes, ct)
                : await _sessionClient.CreateOrGetSessionAsync(candidateId, campaignId, campaign.OrgId, jobCategory, questions, criteria, interviewDeadline,
                    campaign.AdaptiveEnabled, campaign.MaxFollowUps, campaign.MaxQuestions, campaign.MaxDeepPerQuestion, campaign.Language, campaign.Seniority, campaign.RubricVersion, questionDetails, scoringPolicy, campaign.SkipPenalty, campaign.TimeLimitMinutes, ct);

            // ATT1 — đếm lượt CHỈ khi Interview trả về buổi KHÁC buổi membership đang giữ (= buổi MỚI).
            // Vào lại / gọi trùng ra cùng session ⇒ không tăng. Start ném trước dòng này (402/429/502/lỗi
            // sinh câu hỏi) ⇒ chưa có buổi nào ⇒ không tăng. Cùng SaveChanges với SessionId mới bên dưới:
            // không có cửa sổ ghi SessionId mới mà quên đếm. (Hai request song song cùng ra một session
            // mới đều gán CÙNG giá trị tuyệt đối AttemptCount + 1 ⇒ không đếm đôi.)
            var isNewSession = membership.SessionId != session.SessionId;
            if (isNewSession)
                membership.AttemptCount += 1;
            membership.SessionId = session.SessionId;
            // Deadline được chốt lần start đầu; HR đổi slot sau đó không được hồi tố session đang chạy.
            membership.InterviewDeadlineAt ??= interviewDeadline;
            if (membership.InterviewStatus is null or InterviewProgressStatus.NotStarted or InterviewProgressStatus.Abandoned)
            {
                membership.InterviewStatus = InterviewProgressStatus.InProgress;
                // MON1-B1: mốc buổi thi bắt đầu — điểm neo cho sweeper heartbeat (B3) đối chiếu với
                // face_images.captured_at. Chỉ đóng dấu khi ĐANG quan sát chuyển sang InProgress:
                // resume (đã InProgress) không vào khối này ⇒ mốc gốc giữ nguyên; membership cũ
                // (interview_started_at = null, có trước migration) resume cũng KHÔNG bị backdate về
                // thời điểm resume — null = "không biết", trung thực hơn một mốc sai (BK23).
                membership.InterviewStartedAt ??= DateTime.UtcNow;
            }
            membership.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "D2 start: candidate {CandidateId} bắt đầu phỏng vấn campaign {CampaignId} → session {SessionId}",
                candidateId, campaignId, session.SessionId);

            return new StartInterviewResponse
            {
                SessionId = session.SessionId,
                CampaignId = campaignId,
                DeadlineAt = membership.InterviewDeadlineAt,
                // SEC-1: FE kích hoạt proctoring khi campaign bật anti-cheat (độc lập face-verify).
                AntiCheatEnabled = campaign.AntiCheatEnabled,
                // SEC-2: bật face-verify + chưa có ảnh tham chiếu → FE cần nhắc enroll (KHÔNG chặn start, D13/SEC-5).
                FaceEnrollRequired = campaign.FaceVerifyEnabled
                    && string.IsNullOrWhiteSpace(membership.ReferenceImageKey),
                // INT-17: FE dùng cờ này để biết bài có đuôi thích ứng (append nextQuestion sau seed cuối).
                AdaptiveEnabled = campaign.AdaptiveEnabled,
                // ATT1 [C7] — lượt của buổi này (sau khi đếm) + thời lượng cả buổi (null = không tính giờ).
                // R3 — tối thiểu 1: buổi bắt đầu trong cửa sổ "đã migrate, chưa deploy code" mang
                // attempt_count 0 (dữ liệu sửa bằng chạy lại backfill); hạt giống KHÔNG đổi (≤ 1 vẫn 32 byte).
                AttemptNo = Math.Max(1, membership.AttemptCount),
                TimeLimitMinutes = campaign.TimeLimitMinutes,
                // ATT1 [C7] — đề chỉ lộ sau "vào phòng" (begin bên Interview). Start KHÔNG trả nội dung câu;
                // giữ id/orderNo/timeLimitSec để FE dựng marker phòng thi.
                Questions = session.Questions
                    .OrderBy(q => q.OrderNo)
                    .Select(q => new StartQuestionItem
                    {
                        Id = q.Id,
                        OrderNo = q.OrderNo,
                        Content = string.Empty,
                        TimeLimitSec = q.TimeLimitSec
                    }).ToList()
            };
        }

        // ATT1 R1 — phân loại trạng thái THẬT của buổi đang giữ (tên enum SessionStatus phía Interview).
        private enum LiveSessionVerdict { Resume, Submitted, Ended }

        private static LiveSessionVerdict ClassifyLiveSession(CampaignSessionState? live)
        {
            // Client thật không bao giờ trả null (lỗi ⇒ ném). null chỉ đến từ test double ⇒ giữ hành vi cũ.
            if (live is null)
                return LiveSessionVerdict.Resume;
            if (!live.Exists)
                return LiveSessionVerdict.Ended;
            return live.Status switch
            {
                var st when string.Equals(st, "Scored", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(st, "Completed", StringComparison.OrdinalIgnoreCase) => LiveSessionVerdict.Submitted,
                var st when string.Equals(st, "SessionAbandoned", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(st, "Failed", StringComparison.OrdinalIgnoreCase) => LiveSessionVerdict.Ended,
                // GeneratingQuestions / Ready / InProgress / Scoring / không rõ (Interview cũ không trả states)
                // ⇒ vào lại như trước.
                _ => LiveSessionVerdict.Resume,
            };
        }

        private static DateTime? MinDeadline(DateTime? campaignExpiresAt, DateTime slotEndsAt) =>
            campaignExpiresAt is null || slotEndsAt < campaignExpiresAt ? slotEndsAt : campaignExpiresAt;

        private static DateTimeOffset ToVietnamTime(DateTime utc) => VietnamTime.From(utc);

        // ── helpers ──────────────────────────────────────────────────────────────────

        // F5 — snapshot danh tính lên membership khi join. Email LUÔN có (lời mời phát theo email);
        // FullName chỉ có ở đường-2 (parse từ CV lúc sàng — C13/C14), đường-1 mời-thẳng thì null và
        // HR chấp nhận cột tên trống (email vẫn đủ để nhận diện).
        // `??=` chứ không gán đè: dữ liệu HR đã sửa tay trên cv_submission (PATCH C14) không bị
        // một lần join lại ghi ngược về giá trị cũ.
        // FX1 — MỘT hàm cho cả (a) quan hệ membership → invitation và (b) snapshot danh tính F5, gọi ở
        // CẢ HAI nhánh của JoinCampaignAsync. Gộp có chủ đích: tách 2 hàm = 2 chỗ có thể quên gọi ở
        // nhánh idempotent (đúng lỗi F5 suýt mắc), gộp lại thì quên là quên cả hai và test bắt ngay.
        //
        // Hai vế có ngữ nghĩa KHÁC nhau, cố ý:
        //  • InvitationId GHI ĐÈ — "lời mời gần nhất đã dẫn tới join". Lời mời cũ sau reissue (D4) đã
        //    Revoked nên ValidateInvitationUsable chặn trước khi tới đây ⇒ không bao giờ ghi đè bằng
        //    lời mời cũ hơn. Nhờ ghi đè, lời mời MỚI hiện đúng "Joined" thay vì "Sent".
        //  • Email/FullName dùng `??=` — snapshot giữ danh tính BIẾT ĐẦU TIÊN, và quan trọng hơn là
        //    không bao giờ ghi đè giá trị đang có bằng null (đường-1 không có CV ⇒ FullName null).
        //
        // Q7 — SlotId cũng GHI ĐÈ, cùng lý lẽ với InvitationId. Trước đây khung giờ chỉ được ghi lên
        // `campaign_invitations`, KHÔNG chỗ nào chép sang membership, trong khi cả 4 đường đọc lại đọc
        // `membership.SlotId`: guard khung giờ lúc Start, `StartedCount` của slot, và guard "không xoá
        // khung giờ đang có ứng viên thi". Cột luôn NULL ⇒ ứng viên có khung giờ ĐÃ ĐÓNG vẫn Start được
        // (200, trừ credit org thật, deadline rơi về campaign.ExpiresAt), `StartedCount` vĩnh viễn 0,
        // guard xoá slot không bao giờ kích hoạt. Đây là chokepoint DUY NHẤT ghi membership trong
        // service, chạy ở CẢ hai nhánh join → một dòng phủ hết.
        // Vì sao `=` chứ KHÔNG `??=`: sau reissue (D4) lời mời cũ đã Revoked nên ValidateInvitationUsable
        // chặn trước ⇒ không bao giờ ghi đè bằng lời mời cũ hơn. Ngược lại `??=` sẽ đóng băng khung giờ
        // đầu tiên vĩnh viễn — HR đổi/xoá slot rồi phát lại lời mời thì ứng viên kẹt ở slot cũ và
        // `StartInterviewAsync` ném "Không tìm thấy khung giờ phỏng vấn đã được phân", chặn hẳn buổi thi.
        // `inv.SlotId` null vẫn ĐÚNG nghiệp vụ: campaign chưa cấu hình slot nào → AssignSlotsAsync trả
        // null → "không ràng buộc khung giờ" (xem comment AssignSlotsAsync).
        private static void ApplyInvitationLink(CampaignMembership membership, CampaignInvitation inv)
        {
            membership.InvitationId = inv.Id;
            membership.SlotId = inv.SlotId;
            membership.Email ??= inv.Email;
            membership.FullName ??= inv.CvSubmission?.FullName;
        }

        // Tìm membership để cập nhật khi join (D2 idempotent). DB16: dedup theo (campaign, candidate) —
        // membership KHÔNG có email nên bỏ nhánh email cũ. Đường 2 (shortlist): fallback theo cv_submission_id
        // (link shortlist gắn sẵn trên lời mời) khi candidate chưa từng join.
        private async Task<CampaignMembership?> ResolveMembershipAsync(
            CampaignInvitation inv, Guid candidateId, CancellationToken ct)
        {
            var byCandidate = await _db.CampaignMemberships
                .FirstOrDefaultAsync(m => m.CampaignId == inv.CampaignId && m.CandidateId == candidateId, ct);
            if (byCandidate is not null)
                return byCandidate;

            if (inv.CampaignCandidateId is Guid ccid)
            {
                var byCv = await _db.CampaignMemberships
                    .FirstOrDefaultAsync(m => m.CampaignId == inv.CampaignId && m.CvSubmissionId == ccid, ct);
                if (byCv is not null)
                    return byCv;
            }

            return null;
        }

        // Lời mời còn dùng được: chưa revoke, chưa hết hạn, campaign còn Active. Ngược lại → 410 Gone.
        // DB23 — token rỗng/trắng = không tồn tại (404), KHÔNG để lọt xuống Hash() ném ArgumentException
        // (sẽ thành 500). Trim để dung thứ khoảng trắng khi ứng viên copy link từ email.
        private static InterviewProgressStatus MapCandidateInterviewStatus(InterviewProgressStatus? status) =>
            status == InterviewProgressStatus.Abandoned ? InterviewProgressStatus.NotStarted
                : status ?? InterviewProgressStatus.NotStarted;

        private static string HashOrThrow(string token)
        {
            var trimmed = token?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new KeyNotFoundException("Lời mời không tồn tại.");
            return InvitationTokens.Hash(trimmed);
        }

        private static void ValidateInvitationUsable(CampaignInvitation inv)
        {
            if (inv.Campaign is null)
                throw new InvitationGoneException("Chiến dịch không còn khả dụng.");
            if (inv.RevokedAt is not null)
                throw new InvitationGoneException("Lời mời đã bị thu hồi.");
            if (inv.ExpiresAt < DateTime.UtcNow)   // DB23 — luôn có hạn (không còn nhánh NULL = vĩnh viễn)
                throw new InvitationGoneException("Lời mời đã hết hạn.");
            if (inv.Campaign.Status != CampaignStatus.Active)
                throw new InvitationGoneException($"Chiến dịch không còn nhận ứng viên (trạng thái {inv.Campaign.Status}).");
        }

        // Quyết định 7 — KHÔNG map Levels sang ứng viên. Type đích không có trường đó nên đây là
        // ràng buộc của trình biên dịch, không phải của người đọc code.
        private static CandidateCriterionResponse MapCriterion(CampaignCriterion c) => new()
        {
            Id = c.Id,
            OrderNo = c.OrderNo,
            Name = c.Name,
            Description = c.Description,
            Weight = c.Weight,
            MaxScore = c.MaxScore,
            Source = c.Source.ToString()
        };
    }
}
