using Amazon.S3.Model;
using CsvHelper;
using CsvHelper.Configuration;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Pagination;
using Isas.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Isas.CampaignService.Services
{
    public class CampaignService : ICampaignService
    {
        private readonly ILogger<CampaignService> _logger;
        private readonly CampaignDbContext _db;
        private readonly IFileService _file;
        private readonly IParserService _parser;
        private readonly ICriteriaSuggester _suggester;
        private readonly IInvitationEmailPublisher _emailPublisher;
        // AI4 — typed HttpClient gọi Interview /internal/sessions/{sessionId}/answers (transcript cho HR).
        // Optional (default null): giữ nguyên các call-site test 6-tham-số hiện có; DI luôn resolve client
        // thật (đăng ký ở Program.cs). GetSessionTranscriptAsync null → InvalidOperationException (config).
        private readonly ICampaignSessionClient? _sessionClient;
        // DB23 — hạn mặc định cho token khi campaign không có deadline (optional: test cũ gọi
        // ctor 6/7 tham số vẫn compile, dùng default 14 ngày).
        private readonly InvitationSettings _invitationSettings;
        // F9 — typed HttpClient gọi AIService /generate-questions. Optional (default null) để giữ nguyên
        // mọi call-site test hiện có; DI luôn resolve client thật (đăng ký ở Program.cs). null → chỉ ảnh
        // hưởng đúng đường sinh câu hỏi (ném InvalidOperationException = lỗi cấu hình), không đường nào khác.
        private readonly IQuestionGenerator? _questionGenerator;
        private readonly IEntitlementClient? _entitlements;
        private readonly bool _bilingualEnabled;
        private static readonly HashSet<string> AllowedMimeTypes = new()
            {
                "application/pdf",
            };
        private const long MaxFileSizeBytes = 10 * 1024 * 1024;

        public CampaignService(CampaignDbContext db,
            IFileService file, ILogger<CampaignService> logger,
            IParserService parser, ICriteriaSuggester suggester,
            IInvitationEmailPublisher emailPublisher,
            ICampaignSessionClient? sessionClient = null,
            IOptions<InvitationSettings>? invitationOptions = null,
            IQuestionGenerator? questionGenerator = null,
            IEntitlementClient? entitlements = null,
            IConfiguration? config = null)
        {
            _questionGenerator = questionGenerator;
            _invitationSettings = invitationOptions?.Value ?? new InvitationSettings();
            _db = db;
            _file = file;
            _logger = logger;
            _parser = parser;
            _suggester = suggester;
            _emailPublisher = emailPublisher;
            _sessionClient = sessionClient;
            _entitlements = entitlements;
            _bilingualEnabled = bool.TryParse(config?["Campaign:Bilingual:Enabled"], out var bilingual) && bilingual;
        }

        public async Task<CampaignResponse> CreateCampaignAsync(Guid orgId, Guid actorUserId, CreateCampaignRequest request, CancellationToken ct = default)
        {
            var entitlement = await ResolveEntitlementAsync(orgId, ct);
            await EnsureCanCreateCampaignAsync(orgId, entitlement, ct);
            ValidateEntitledSelection(request.MaxCandidates, request.AdaptiveEnabled, request.GroundingEnabled, entitlement);
            // ── 1. Validate questions ───────────────────────────
            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                throw new ArgumentException("All questions must have non-empty text.");

            ValidatePassScorePct(request.PassScorePct);   // E5: ngưỡng ∈ [0,100] nếu có
            ValidateAdaptiveCaps(request.MaxFollowUps, request.MaxQuestions, request.MaxDeepPerQuestion);   // INT-17: trần ≥ 0 nếu có
            ValidateConcurrencyCap(request.MaxConcurrentInterviews);

            // C11 + cap độ dài: chuẩn hoá & kiểm ngưỡng TRƯỚC khi dựng entity/ghi DB → vượt ngưỡng thì
            // 400 mà không để lại gì nửa vời.
            var jdText = NormalizeText(request.JdText, JdTextLabel);
            var criteriaText = NormalizeText(request.CriteriaText, CriteriaTextLabel);

            // ── 2. Build campaign entity ────────────────────────
            var campaign = new Campaign
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                Title = request.Title,
                Domain = request.Domain,
                Language = ValidateLanguage(request.Language),
                Status = CampaignStatus.Draft,
                MaxCandidates = request.MaxCandidates,
                TimeLimitMinutes = request.TimeLimitMinutes,
                AntiCheatEnabled = request.AntiCheatEnabled,
                AdaptiveEnabled = request.AdaptiveEnabled,   // INT-17: HR bật thích ứng cho campaign
                GroundingEnabled = request.GroundingEnabled,
                MaxConcurrentInterviews = request.MaxConcurrentInterviews,
                MaxFollowUps = request.MaxFollowUps,
                MaxQuestions = request.MaxQuestions,
                MaxDeepPerQuestion = request.MaxDeepPerQuestion,   // INT-17b: trần đào sâu mỗi câu
                FaceVerifyEnabled = request.FaceVerifyEnabled,   // SEC-1: face-verify opt-in (B2B)
                PassScorePct = request.PassScorePct,   // E5: ngưỡng pass/fail (null = HR quyết tay)
                // C11: JD/Criteria nhập text trực tiếp → *_text set, *_file_url null (không file lúc tạo).
                JDText = jdText,
                CriteriaText = criteriaText,
                StartsAt = request.StartsAt,
                ExpiresAt = request.ExpiresAt,
                // Set trong code (như AddAudit/questions) → chạy được trên SQLite test + Postgres,
                // không phụ thuộc default DB `now()`.
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            // ── 3. Build questions ──────────────────────────────
            // F10: `source` ép CustomHr, KHÔNG lấy từ client. Campaign vừa tạo thì chưa lần nào gọi AI,
            // nên câu gắn nhãn `AiGenerated` ở đây là nhãn sai theo định nghĩa. Tệ hơn: F9 khi sinh sẽ
            // xoá mọi row `AiGenerated` cũ ⇒ câu HR gõ mà tự nhận là AI sẽ bị lượt sinh kế nuốt mất.
            campaign.Questions = request.Questions
                .Select(q => new CampaignQuestion
                {
                    // Id sinh ở app (như F9/F10), không nhờ default `gen_random_uuid()` của Postgres:
                    // 3 đường ghi câu hỏi nay thống nhất, và đường create thành test được trên SQLite
                    // (trước đây không → `CampaignStructuredCriteriaTests` phải né hẳn việc seed câu hỏi).
                    Id = Guid.NewGuid(),
                    OrgId = orgId,
                    QuestionText = q.QuestionText.Trim(),
                    Source = QuestionSource.CustomHr,
                    IsRequired = q.IsRequired,
                    CreatedAt = DateTime.UtcNow,
                })
                .ToList();

            // ── 3b. C12: tiêu chí structured HR khai thẳng (nếu có) → validate + build (HrEdited).
            // Campaign mới luôn Draft nên set trực tiếp; input hỏng → ArgumentException (→400).
            if (request.Criteria is not null)
            {
                campaign.Criteria = BuildStructuredCriteria(campaign.Id, request.Criteria);
                AddAudit(actorUserId, orgId, AuditAction.EditCriteria, campaign.Id, $"Khai {campaign.Criteria.Count} tiêu chí (HrEdited)");
            }

            // ── 4. Persist campaign + audit (C10) ───────────────
            _db.Campaigns.Add(campaign);
            AddAudit(actorUserId, orgId, AuditAction.CreateCampaign, campaign.Id, $"Tạo campaign '{campaign.Title}'");
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<IReadOnlyList<CampaignSlotResponse>> GetSlotsAsync(Guid orgId, Guid campaignId, CancellationToken ct)
        {
            await RequireCampaignAsync(orgId, campaignId, ct);
            return await _db.CampaignSlots.Where(x => x.CampaignId == campaignId).OrderBy(x => x.StartsAt)
                .Select(x => new CampaignSlotResponse { Id = x.Id, StartsAt = x.StartsAt, EndsAt = x.EndsAt, Capacity = x.Capacity,
                    AssignedCount = _db.CampaignInvitations.Count(i => i.SlotId == x.Id && i.RevokedAt == null),
                    StartedCount = _db.CampaignMemberships.Count(m => m.SlotId == x.Id && m.InterviewStatus == InterviewProgressStatus.InProgress) }).ToListAsync(ct);
        }

        public async Task<CampaignSlotResponse> CreateSlotAsync(Guid orgId, Guid campaignId, CreateCampaignSlotRequest request, CancellationToken ct)
        {
            await RequireCampaignAsync(orgId, campaignId, ct); ValidateSlot(request.StartsAt, request.EndsAt, request.Capacity);
            await EnsureNoSlotOverlapAsync(campaignId, request.StartsAt, request.EndsAt, null, ct);
            var slot = new CampaignSlot { Id = Guid.NewGuid(), CampaignId = campaignId, StartsAt = request.StartsAt, EndsAt = request.EndsAt, Capacity = request.Capacity };
            _db.CampaignSlots.Add(slot); await _db.SaveChangesAsync(ct); return ToSlotResponse(slot, 0, 0);
        }

        public async Task<CampaignSlotResponse> UpdateSlotAsync(Guid orgId, Guid campaignId, Guid slotId, UpdateCampaignSlotRequest request, CancellationToken ct)
        {
            await RequireCampaignAsync(orgId, campaignId, ct); ValidateSlot(request.StartsAt, request.EndsAt, request.Capacity);
            var slot = await _db.CampaignSlots.FirstOrDefaultAsync(x => x.Id == slotId && x.CampaignId == campaignId, ct) ?? throw new KeyNotFoundException();
            var assigned = await _db.CampaignInvitations.CountAsync(i => i.SlotId == slotId && i.RevokedAt == null, ct);
            if (request.Capacity < assigned) throw new ArgumentException("Sức chứa không thể nhỏ hơn số lời mời đã gán.");
            await EnsureNoSlotOverlapAsync(campaignId, request.StartsAt, request.EndsAt, slotId, ct);
            slot.StartsAt=request.StartsAt; slot.EndsAt=request.EndsAt; slot.Capacity=request.Capacity; await _db.SaveChangesAsync(ct);
            var started=await _db.CampaignMemberships.CountAsync(m=>m.SlotId==slotId&&m.InterviewStatus==InterviewProgressStatus.InProgress,ct); return ToSlotResponse(slot,assigned,started);
        }

        public async Task DeleteSlotAsync(Guid orgId, Guid campaignId, Guid slotId, CancellationToken ct)
        {
            await RequireCampaignAsync(orgId,campaignId,ct); var slot=await _db.CampaignSlots.FirstOrDefaultAsync(x=>x.Id==slotId&&x.CampaignId==campaignId,ct)??throw new KeyNotFoundException();
            if(await _db.CampaignMemberships.AnyAsync(m=>m.SlotId==slotId&&m.InterviewStatus==InterviewProgressStatus.InProgress,ct)) throw new InvalidOperationException("Không thể xóa khung giờ đang có ứng viên thi.");
            _db.CampaignSlots.Remove(slot); await _db.SaveChangesAsync(ct);
        }

        private async Task RequireCampaignAsync(Guid orgId, Guid campaignId, CancellationToken ct) => _ = await _db.Campaigns.FirstOrDefaultAsync(c=>c.Id==campaignId&&c.OrgId==orgId,ct) ?? throw new KeyNotFoundException();
        private static void ValidateSlot(DateTime starts, DateTime ends, int capacity) { if(ends<=starts||capacity<=0) throw new ArgumentException("Khung giờ hoặc sức chứa không hợp lệ."); }
        private async Task EnsureNoSlotOverlapAsync(Guid campaignId, DateTime starts, DateTime ends, Guid? exceptId, CancellationToken ct) { if(await _db.CampaignSlots.AnyAsync(s=>s.CampaignId==campaignId&&s.Id!=exceptId&&s.StartsAt<ends&&starts<s.EndsAt,ct)) throw new InvalidOperationException("Khung giờ bị chồng lấn."); }
        private static CampaignSlotResponse ToSlotResponse(CampaignSlot x,int assigned,int started)=>new(){Id=x.Id,StartsAt=x.StartsAt,EndsAt=x.EndsAt,Capacity=x.Capacity,AssignedCount=assigned,StartedCount=started};

        public async Task<CampaignResponse> UploadCampaignFilesAsync(Guid orgId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct = default)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException();

            // C11: text ưu tiên file — bỏ file cho slot đã nhập text trực tiếp (*_text set, chưa gắn file).
            var jdFile = HasDirectText(campaign.JDText, campaign.JDFileUrl) ? null : request.JdFile;
            var criteriaFile = HasDirectText(campaign.CriteriaText, campaign.CriteriaFileUrl) ? null : request.CriteriaFile;

            if (jdFile is not null) ValidateFile(jdFile, "JD");
            if (criteriaFile is not null) ValidateFile(criteriaFile, "Criteria");

            var jdTask = HandleFileAsync(jdFile, campaign.Id, "jd", ct);
            var criteriaTask = HandleFileAsync(criteriaFile, campaign.Id, "criteria", ct);

            var results = await Task.WhenAll(jdTask, criteriaTask);

            foreach (var result in results.Where(r => r is not null))
            {
                var value = result.Value;
                if (value.Label == "jd")
                {
                    campaign.JDFileUrl = value.Url;
                    campaign.JDText = value.Text;
                }
                else if (value.Label == "criteria")
                {
                    campaign.CriteriaFileUrl = value.Url;
                    campaign.CriteriaText = value.Text;
                }
            }

            _db.Campaigns.Update(campaign);
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }
         
        public async Task<Stream> DownloadCampaignFilesAsync(Guid orgId, Guid id, string fileType, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            string? fileUrl = fileType.ToLower() switch
            {
                "jd" => campaign.JDFileUrl,
                "criteria" => campaign.CriteriaFileUrl,
                _ => throw new ArgumentException("Invalid file type. Must be 'jd' or 'criteria'.")
            };

            if (string.IsNullOrWhiteSpace(fileUrl))
                throw new FileNotFoundException($"No {fileType} file found for campaign {id}.");

            return await _file.DownloadAsync(fileUrl, ct);
        }

        public async Task<CampaignResponse> GetCampaignAsync(Guid orgId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)   // C12: trả tiêu chí structured để HR xem/duyệt
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            return CampaignResponse.FromEntity(campaign);
        }

        // List campaign của Employer — endpoint user THẬT SỰ gọi (khác admin oversight bên dưới).
        // DB31: trước đây trả TOÀN BỘ campaign của org, không phân trang, kèm 2 Include → org lâu năm
        // kéo cả nghìn campaign × (questions + criteria) mỗi lần mở trang. Nay keyset-paged theo ĐÚNG
        // convention DB8 (`ListAllCampaignsAsync` ngay dưới): cursor opaque `(CreatedAt DESC, Id DESC)`,
        // limit mặc định 500 = hành vi cũ, body vẫn mảng JSON, next-cursor ở header X-Next-Cursor.
        // Index `(org_id, created_at, id)` (DB26) phủ trọn khoá sắp xếp này.
        public async Task<KeysetPage<CampaignResponse>> GetCampaignsAsync(
            Guid orgId, string? cursor, int? limit, CancellationToken ct)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _db.Campaigns.Where(c => c.OrgId == orgId);

            if (cur is not null)
                query = query.Where(c => c.CreatedAt < cur.CreatedAt
                    || (c.CreatedAt == cur.CreatedAt && c.Id.CompareTo(cur.Id) < 0));

            var rows = await query
                .Include(c => c.Questions)
                .Include(c => c.Criteria)   // list card hiện đúng số tiêu chí (khớp detail — C12)
                // 2 Include collection trên cùng 1 root = JOIN fan-out nhân bản dòng gốc
                // (questions × criteria) rồi EF dedup ở client. Split query tách thành 3 câu lệnh
                // gọn: đúng thứ tự/limit được EF áp lại cho từng câu, nên phân trang vẫn chuẩn.
                .AsSplitQuery()
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Take(take)
                .ToListAsync(ct);

            var items = rows.Select(CampaignResponse.FromEntity).ToList();
            var next = rows.Count == take
                ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
                : null;
            return new KeysetPage<CampaignResponse>(items, next);
        }

        // AUTH-7: PlatformAdmin oversight — MỌI campaign xuyên org (KHÔNG lọc org_id, khác GetCampaignsAsync).
        // Soft-delete (D11) tự loại nhờ global query filter (DeletedAt==null trên _db.Campaigns). Optional
        // lọc status (parse enum; giá trị lạ → không match → rỗng) + orgId. Keyset-paged (DB8): mới nhất
        // trước theo (CreatedAt DESC, Id DESC); cursor rỗng = trang đầu; limit mặc định 500 (giữ hành vi cũ).
        public async Task<KeysetPage<AdminCampaignListItem>> ListAllCampaignsAsync(
            string? status, Guid? orgId, string? cursor, int? limit, CancellationToken ct)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _db.Campaigns.AsQueryable();

            if (orgId is Guid oid)
                query = query.Where(c => c.OrgId == oid);

            if (!string.IsNullOrWhiteSpace(status)
                && Enum.TryParse<CampaignStatus>(status.Trim(), ignoreCase: true, out var parsed))
                query = query.Where(c => c.Status == parsed);

            if (cur is not null)
                query = query.Where(c => c.CreatedAt < cur.CreatedAt
                    || (c.CreatedAt == cur.CreatedAt && c.Id.CompareTo(cur.Id) < 0));

            var rows = await query
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Take(take)
                .ToListAsync(ct);

            var items = rows.Select(AdminCampaignListItem.FromEntity).ToList();
            var next = rows.Count == take
                ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
                : null;
            return new KeysetPage<AdminCampaignListItem>(items, next);
        }

        public async Task<CampaignResponse> UpdateCampaignAsync(Guid orgId, Guid actorUserId, Guid id, UpdateCampaignRequest request, CancellationToken ct)
        {
            // ── 0. Cap độ dài JD/tiêu chí nhập text — kiểm TRƯỚC cả fetch: guard rẻ nhất chạy đầu,
            // vượt ngưỡng → 400 mà không tốn round-trip DB và không đụng entity nào.
            var jdText = NormalizeText(request.JdText, JdTextLabel);
            var criteriaText = NormalizeText(request.CriteriaText, CriteriaTextLabel);

            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            var entitlement = await ResolveEntitlementAsync(orgId, ct);
            ValidateEntitledMutation(request.MaxCandidates, request.AdaptiveEnabled, request.GroundingEnabled, entitlement);

            // ── 2. Only update fields that were actually provided
            if (request.Title is not null)
                campaign.Title = request.Title;

            if (request.Domain is not null)
                campaign.Domain = request.Domain;

            if (request.Language is not null)
            {
                if (campaign.Status != CampaignStatus.Draft)
                    throw new InvalidOperationException("Chỉ được đổi language khi campaign ở Draft.");
                campaign.Language = ValidateLanguage(request.Language);
            }

            if (request.MaxCandidates.HasValue)
                campaign.MaxCandidates = request.MaxCandidates;

            if (request.TimeLimitMinutes.HasValue)
                campaign.TimeLimitMinutes = request.TimeLimitMinutes;

            if (request.AntiCheatEnabled.HasValue)
                campaign.AntiCheatEnabled = request.AntiCheatEnabled.Value;

            // SEC-1: merge-only-if-provided (như AntiCheatEnabled C3) — null giữ nguyên giá trị cũ.
            if (request.FaceVerifyEnabled.HasValue)
                campaign.FaceVerifyEnabled = request.FaceVerifyEnabled.Value;

            // E5: cập nhật ngưỡng pass/fail (chỉ khi gửi lên; validate ∈ [0,100]).
            if (request.PassScorePct.HasValue)
            {
                ValidatePassScorePct(request.PassScorePct);
                campaign.PassScorePct = request.PassScorePct;
            }

            // INT-17: null = KHÔNG đổi (giữ giá trị cũ), như AntiCheatEnabled/FaceVerifyEnabled.
            if (request.AdaptiveEnabled.HasValue)
                campaign.AdaptiveEnabled = request.AdaptiveEnabled.Value;

            if (request.GroundingEnabled.HasValue)
                campaign.GroundingEnabled = request.GroundingEnabled.Value;

            if (request.MaxConcurrentInterviews.HasValue)
            {
                ValidateConcurrencyCap(request.MaxConcurrentInterviews);
                campaign.MaxConcurrentInterviews = request.MaxConcurrentInterviews;
            }

            if (request.MaxFollowUps.HasValue || request.MaxQuestions.HasValue
                || request.MaxDeepPerQuestion.HasValue)
            {
                ValidateAdaptiveCaps(request.MaxFollowUps, request.MaxQuestions, request.MaxDeepPerQuestion);
                if (request.MaxFollowUps.HasValue) campaign.MaxFollowUps = request.MaxFollowUps;
                if (request.MaxQuestions.HasValue) campaign.MaxQuestions = request.MaxQuestions;
                if (request.MaxDeepPerQuestion.HasValue) campaign.MaxDeepPerQuestion = request.MaxDeepPerQuestion;
            }

            // C11: cập nhật JD/Criteria dạng text → set *_text, xoá *_file_url (text ưu tiên file).
            if (request.JdText is not null)
            {
                campaign.JDText = jdText;   // đã chuẩn hoá + kiểm ngưỡng ở bước 0
                campaign.JDFileUrl = null;
            }

            if (request.CriteriaText is not null)
            {
                campaign.CriteriaText = criteriaText;   // đã chuẩn hoá + kiểm ngưỡng ở bước 0
                campaign.CriteriaFileUrl = null;
            }

            // C12: ghi đè tiêu chí structured. Chỉ khi Draft (Active → 409);
            // validate → 400 (ArgumentException) TRƯỚC khi đụng DB để lỗi không để lại nửa vời.
            List<CampaignCriterion>? rebuiltCriteria = null;
            if (request.Criteria is not null)
            {
                if (campaign.Status != CampaignStatus.Draft)
                    throw new InvalidOperationException(
                        $"Cannot edit criteria when campaign is {campaign.Status}. Only Draft is editable.");

                rebuiltCriteria = BuildStructuredCriteria(campaign.Id, request.Criteria);
            }

            if (request.StartsAt.HasValue)
                campaign.StartsAt = request.StartsAt;

            if (request.ExpiresAt.HasValue)
                campaign.ExpiresAt = request.ExpiresAt;

            // ── 3. Persist ───────────────────────────────────────
            campaign.UpdatedAt = DateTime.UtcNow;

            if (rebuiltCriteria is null)
            {
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                // Replace-all ATOMIC (1 SaveChanges = 1 transaction): XOÁ bộ cũ + INSERT bộ mới qua DbSet.
                // KHÔNG đụng navigation (nav.Clear()/Add trên quan hệ required làm change-tracker sinh
                // UPDATE "ma" → DbUpdateConcurrencyException 0 rows). EF tự xếp DELETE trước INSERT theo
                // UNIQUE(campaign_id, order_no|name) nên bộ mới trùng khoá bộ cũ vẫn an toàn.
                _db.CampaignCriteria.RemoveRange(campaign.Criteria);
                _db.CampaignCriteria.AddRange(rebuiltCriteria);
                AddAudit(actorUserId, orgId, AuditAction.EditCriteria, campaign.Id, $"Ghi đè {rebuiltCriteria.Count} tiêu chí (HrEdited)");
                await _db.SaveChangesAsync(ct);
                campaign.Criteria = rebuiltCriteria;                 // đồng bộ nav cho response (bộ cũ đã xoá)
            }

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<CampaignResponse> UpdateCampaignFilesAsync(Guid orgId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct = default)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // ── Lifecycle (C7): chỉ thay JD/Criteria khi Draft ─
            if (campaign.Status != CampaignStatus.Draft)
                throw new InvalidOperationException($"Cannot edit files when campaign is {campaign.Status}. Only Draft is editable.");

            if (request.JdFile is null && request.CriteriaFile is null)
                throw new ArgumentException("At least one file must be provided.");

            // C11: text ưu tiên file — bỏ file cho slot đã nhập text trực tiếp (*_text set, chưa gắn file).
            var jdFile = HasDirectText(campaign.JDText, campaign.JDFileUrl) ? null : request.JdFile;
            var criteriaFile = HasDirectText(campaign.CriteriaText, campaign.CriteriaFileUrl) ? null : request.CriteriaFile;

            if (jdFile is not null) ValidateFile(jdFile, "JD");
            if (criteriaFile is not null) ValidateFile(criteriaFile, "Criteria");

            // ── Delete old files from SeaweedFS before uploading ─
            if (jdFile is not null && !string.IsNullOrWhiteSpace(campaign.JDFileUrl))
                await _file.DeleteAsync(campaign.JDFileUrl, ct);

            if (criteriaFile is not null && !string.IsNullOrWhiteSpace(campaign.CriteriaFileUrl))
                await _file.DeleteAsync(campaign.CriteriaFileUrl, ct);

            // ── Upload new files ──────────────────────────────────
            var jdTask = HandleFileAsync(jdFile, campaign.Id, "jd", ct);
            var criteriaTask = HandleFileAsync(criteriaFile, campaign.Id, "criteria", ct);

            var results = await Task.WhenAll(jdTask, criteriaTask);

            foreach (var result in results.Where(r => r is not null))
            {
                var value = result.Value;
                if (value.Label == "jd")
                {
                    campaign.JDFileUrl = value.Url;
                    campaign.JDText = value.Text;
                }
                else if (value.Label == "criteria")
                {
                    campaign.CriteriaFileUrl = value.Url;
                    campaign.CriteriaText = value.Text;
                }
            }

            campaign.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<CampaignResponse> UpdateCampaignQuestionsAsync(Guid orgId, Guid actorUserId, Guid id, List<QuestionItem> questions, CancellationToken ct)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // ── Lifecycle (C7): chỉ sửa câu hỏi khi Draft (Active rồi → khóa) ─
            if (campaign.Status != CampaignStatus.Draft)
                throw new InvalidOperationException($"Cannot edit questions when campaign is {campaign.Status}. Only Draft is editable.");

            // ── 2. Validate questions ───────────────────────────
            if (questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                throw new ArgumentException("All questions must have non-empty text.");

            // ── 3. F10 — MERGE theo id thay vì Clear()+tạo lại ──────────────────────────────
            //    Cũ: xoá sạch rồi dựng lại với Guid mới + `source` lấy từ client. Vì FE hardcode
            //    `source:'CustomHr'`, HR sửa MỘT câu là toàn bộ câu F9 sinh mất nhãn `AiGenerated`
            //    VÀ mất id (mọi tham chiếu tới câu hỏi đứt, thứ tự bài thi đảo).
            //    Mới: id có trong payload → sửa tại chỗ; vắng mặt → xoá; không id → thêm mới (CustomHr).
            var existing = campaign.Questions.ToDictionary(q => q.Id);
            var keptIds = new HashSet<Guid>();
            var fresh = new List<CampaignQuestion>();
            var now = DateTime.UtcNow;

            foreach (var item in questions)
            {
                var text = item.QuestionText.Trim();

                if (item.Id is Guid qid && qid != Guid.Empty)
                {
                    // Id lạ = client đang nói về một câu không thuộc campaign này (id của campaign khác,
                    // hoặc câu vừa bị người khác xoá). Im lặng coi như "thêm mới" sẽ NUỐT MẤT ý định sửa
                    // và đẻ ra câu trùng → 400 để client biết state của mình đã cũ.
                    if (!existing.TryGetValue(qid, out var row))
                        throw new ArgumentException($"Question {qid} không thuộc campaign {id} (hoặc đã bị xoá).");

                    // Cùng một id gửi 2 lần: "sửa" nào thắng là không xác định → chặn thay vì đoán.
                    if (!keptIds.Add(qid))
                        throw new ArgumentException($"Question {qid} xuất hiện nhiều lần trong payload.");

                    row.QuestionText = text;
                    row.IsRequired = item.IsRequired;
                    // KHÔNG gán row.Source: nguồn gốc là sự thật do server ghi lúc tạo (F9 = AiGenerated,
                    // HR gõ tay = CustomHr). Cho client ghi đè thì nhãn nguồn thành lời khai tự do.
                    // KHÔNG gán row.CreatedAt: thứ tự bài thi sắp theo (CreatedAt, Id) — xem ParticipationService.
                }
                else
                {
                    fresh.Add(new CampaignQuestion
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaign.Id,
                        OrgId = campaign.OrgId,
                        QuestionText = text,
                        Source = QuestionSource.CustomHr,   // F10: câu mới qua đường PUT = HR gõ tay, luôn
                        IsRequired = item.IsRequired,
                        CreatedAt = now,
                    });
                }
            }

            // Câu đang có mà payload không nhắc tới = HR đã xoá trên UI (PUT = replace).
            var removed = existing.Values.Where(q => !keptIds.Contains(q.Id)).ToList();
            _db.CampaignQuestions.RemoveRange(removed);
            foreach (var q in removed)
                campaign.Questions.Remove(q);   // để response phản ánh đúng đề sau khi sửa

            // DbSet.AddRange chứ KHÔNG campaign.Questions.Add(): `Id` là store-generated, entity mang Id
            // khác default mà chỉ gắn vào navigation sẽ bị DetectChanges phân loại Modified → UPDATE 0 row
            // → DbUpdateConcurrencyException (bẫy đã dính ở F9). AddRange ép state = Added; relationship
            // fixup tự đưa vào campaign.Questions cho response.
            _db.CampaignQuestions.AddRange(fresh);

            campaign.UpdatedAt = now;
            AddAudit(actorUserId, orgId, AuditAction.EditQuestions, campaign.Id,
                $"Sửa câu hỏi: giữ {keptIds.Count}, thêm {fresh.Count}, xoá {removed.Count}");
            await _db.SaveChangesAsync(ct);
            return CampaignResponse.FromEntity(campaign);
        }

        // F9 (FR11) — AI sinh câu hỏi từ JD cho campaign B2B.
        // Trần số câu 1 lượt sinh: giữ bounded chi phí token + độ dài bài thi so sánh được (E1 fairness).
        private const int MaxGeneratedQuestions = 20;

        public async Task<CampaignResponse> GenerateCampaignQuestionsAsync(
            Guid orgId, Guid actorUserId, Guid id, int? count, CancellationToken ct)
        {
            // ── 1. Fetch & verify ownership (ngoài org → 404, không lộ tồn tại) ──
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // ── 2. CAMP-2: câu hỏi chỉ sửa được khi Draft; Active/Closed/Archived → 409 ──
            //    Sinh câu hỏi CŨNG là sửa câu hỏi → phải chịu cùng khoá lifecycle, nếu không
            //    thì đây thành cửa hậu đổi đề của chiến dịch ĐANG chạy (ứng viên đã làm bài).
            if (campaign.Status != CampaignStatus.Draft)
                throw new InvalidOperationException(
                    $"Cannot generate questions when campaign is {campaign.Status}. Only Draft is editable.");

            // ── 3. Cần JD: không có JD thì "sinh từ JD" không có nghĩa → 400, KHÔNG tốn 1 lời gọi AI ──
            //    (CAMP-5: JD nhập text trực tiếp hoặc trích từ PDF; cả 2 đường đều đổ vào JDText.)
            if (string.IsNullOrWhiteSpace(campaign.JDText))
                throw new ArgumentException(
                    "Campaign chưa có JD (jdText) — cần JD để AI sinh câu hỏi.");

            // ── 4. Guard độ dài JD TRƯỚC khi gọi AI (CAMP-5, ngưỡng chung TextInputLimits.JdTextMaxChars) ──
            //    Text lúc ghi đã bị cap, nhưng campaign tạo trước khi có cap / sửa thẳng DB vẫn có thể vượt
            //    → guard lại ở đây để không đẩy khối text tuỳ ý vào một lời gọi Gemini tính phí.
            var jdText = NormalizeText(campaign.JDText, JdTextLabel);

            if (count is not null && (count < 1 || count > MaxGeneratedQuestions))
                throw new ArgumentException($"count phải trong khoảng 1..{MaxGeneratedQuestions}.");

            if (_questionGenerator is null)
                throw new InvalidOperationException("Question generator chưa được cấu hình.");

            // ── 5. Gọi AI (AI-4: jdText là DỮ LIỆU — AIService đã bọc delimiter + chỉ thị bỏ qua lệnh
            //    nhúng trong JD). Lỗi upstream → DownstreamServiceException → controller map 502. ──
            var jobCategory = string.IsNullOrWhiteSpace(campaign.Domain) ? "BE" : campaign.Domain!;
            var generated = await _questionGenerator.GenerateAsync(jobCategory, jdText, count, ct);

            // AI trả rỗng = lượt sinh không dùng được. Trả 502 thay vì lặng lẽ xoá sạch đề cũ rồi
            // báo thành công — HR phải biết là AI hỏng, không phải "campaign của tôi mất hết câu hỏi".
            if (generated.Count == 0)
                throw new DownstreamServiceException("AIService không sinh được câu hỏi nào từ JD này.");

            // Cắt bớt phải NÓI RA. Im lặng truncate đọc thành "AI sinh đúng ngần này" trong khi thực tế
            // có câu bị rơi — người vận hành không có cách nào biết trần đang cắn.
            if (generated.Count > MaxGeneratedQuestions)
            {
                _logger.LogWarning(
                    "F9 — AIService sinh {Actual} câu cho campaign {CampaignId}, vượt trần {Max} → bỏ {Dropped} câu.",
                    generated.Count, campaign.Id, MaxGeneratedQuestions, generated.Count - MaxGeneratedQuestions);
                generated = generated.Take(MaxGeneratedQuestions).ToList();
            }

            // ── 6. Lưu: thay lượt AI trước đó, GIỮ NGUYÊN câu HR tự gõ ──────────────
            //    "Sinh lại" = làm mới đề AI, không phải cộng dồn (bấm 3 lần ≠ 15 câu), và tuyệt đối
            //    không được nuốt công HR đã gõ tay. F10 mới là phần trộn qua đường PUT questions.
            var aiOld = campaign.Questions.Where(q => q.Source == QuestionSource.AiGenerated).ToList();
            _db.CampaignQuestions.RemoveRange(aiOld);
            foreach (var q in aiOld)
                campaign.Questions.Remove(q);   // để response phản ánh đúng đề sau khi sinh

            var now = DateTime.UtcNow;
            var fresh = generated.Select(text => new CampaignQuestion
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                OrgId = campaign.OrgId,
                QuestionText = text,
                Source = QuestionSource.AiGenerated,   // F9: dấu vết nguồn — phân biệt với câu HR gõ
                IsRequired = true,
                CreatedAt = now,
            }).ToList();

            // Dùng DbSet.AddRange chứ KHÔNG campaign.Questions.Add(): Id của câu hỏi là store-generated,
            // nên entity mang Id khác default mà chỉ gắn vào navigation sẽ bị DetectChanges phân loại là
            // Modified (EF tưởng là row đã tồn tại) → UPDATE 0 row → DbUpdateConcurrencyException.
            // AddRange ép state = Added; relationship fixup tự đưa vào campaign.Questions cho response.
            _db.CampaignQuestions.AddRange(fresh);

            campaign.UpdatedAt = now;
            AddAudit(actorUserId, orgId, AuditAction.EditQuestions, campaign.Id,
                $"AI sinh {generated.Count} câu hỏi từ JD (thay {aiOld.Count} câu AI cũ)");
            await _db.SaveChangesAsync(ct);
            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<bool> DeleteCampaignAsync(Guid orgId, Guid actorUserId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // Soft delete (D11): giữ campaign + câu hỏi + file cho audit/đối chất.
            // KHÔNG xoá file ngay — cronjob purge SeaweedFS sau 90 ngày.
            campaign.DeletedAt = DateTime.UtcNow;
            campaign.UpdatedAt = DateTime.UtcNow;
            AddAudit(actorUserId, orgId, AuditAction.Delete, campaign.Id, "Soft delete");
            await _db.SaveChangesAsync(ct);
            return true;
        }

        // ── PUBLISH (C8): Draft → Active + sinh tiêu chí CÓ CẤU TRÚC ────────
        public async Task<CampaignResponse> PublishCampaignAsync(Guid orgId, Guid actorUserId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            if (campaign.Status != CampaignStatus.Draft)
                throw new InvalidOperationException($"Chỉ publish được campaign `Draft` (hiện: {campaign.Status}).");

            await EnsureCanCreateCampaignAsync(orgId, await ResolveEntitlementAsync(orgId, ct), ct);

            if (campaign.Questions.Count == 0)
                throw new InvalidOperationException("Campaign phải có ≥1 câu hỏi trước khi publish.");

            // D9/C8: tiêu chí text → CÓ CẤU TRÚC. Gọi AIService /suggest-criteria (fallback default nếu lỗi).
            if (campaign.Criteria.Count == 0)
                _db.CampaignCriteria.AddRange(await BuildCriteriaAsync(campaign, ct));

            campaign.Status = CampaignStatus.Active;
            campaign.UpdatedAt = DateTime.UtcNow;
            AddAudit(actorUserId, orgId, AuditAction.Publish, campaign.Id, "Publish: Draft → Active");
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        // ── TRANSITION (C7): chỉ tiến Active→Closed→Archived (Draft→Active dùng publish) ──
        public async Task<CampaignResponse> TransitionStatusAsync(Guid orgId, Guid actorUserId, Guid id, CampaignStatus target, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            bool ok = (campaign.Status, target) switch
            {
                (CampaignStatus.Active, CampaignStatus.Closed) => true,
                (CampaignStatus.Closed, CampaignStatus.Archived) => true,
                _ => false
            };
            if (!ok)
                throw new InvalidOperationException(
                    $"Chuyển trạng thái không hợp lệ {campaign.Status} → {target}. (Draft→Active dùng /publish; chỉ tiến Active→Closed→Archived.)");

            var from = campaign.Status;
            campaign.Status = target;
            campaign.UpdatedAt = DateTime.UtcNow;
            AddAudit(actorUserId, orgId, AuditAction.TransitionStatus, campaign.Id, $"{from} → {target}");
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        // ── D1: Distribution đường 1 — mời thẳng qua danh sách email ────────
        // Thứ tự xử lý (đúng doc): validate định dạng → dedup → cap max_candidates.
        // Email hỏng/trùng/đã mời → failed[] per-item, KHÔNG chặn cả batch.
        // Vượt cap max_candidates → chặn CẢ request (ArgumentException → 400).
        public async Task<CreateInvitationsResponse> CreateInvitationsAsync(Guid orgId, Guid actorUserId, Guid id, List<string> emails, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            if (campaign.Status != CampaignStatus.Active)
                throw new InvalidOperationException($"Chỉ mời ứng viên khi campaign đang Active (hiện: {campaign.Status}).");

            var response = new CreateInvitationsResponse();

            // ── 1. Validate định dạng + dedup TRONG list gửi lên ─────────────
            var seenInRequest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();
            foreach (var raw in emails ?? new List<string>())
            {
                var email = raw?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
                {
                    response.Failed.Add(new FailedInvitationItem { Email = raw ?? string.Empty, Reason = "Định dạng email không hợp lệ." });
                    continue;
                }

                if (!seenInRequest.Add(email))
                {
                    response.Failed.Add(new FailedInvitationItem { Email = email, Reason = "Trùng lặp trong danh sách gửi." });
                    continue;
                }

                candidates.Add(email);
            }

            // ── 2. Dedup với invitation đã có (chưa bị revoke) của campaign ──
            var existingEmails = await _db.CampaignInvitations
                .Where(i => i.CampaignId == id && i.RevokedAt == null)
                .Select(i => i.Email)
                .ToListAsync(ct);
            var existingSet = new HashSet<string>(existingEmails, StringComparer.OrdinalIgnoreCase);

            var toCreate = new List<string>();
            foreach (var email in candidates)
            {
                if (existingSet.Contains(email))
                {
                    response.Failed.Add(new FailedInvitationItem { Email = email, Reason = "Email đã được mời." });
                    continue;
                }
                toCreate.Add(email);
            }

            // ── 3. Cap theo max_candidates — vượt → chặn CẢ request (không tạo dở dang) ──
            await EnsureCandidateCapacityAsync(orgId, campaign, existingEmails.Count, toCreate.Count, "lời mời", ct);
            var assignedSlots = await AssignSlotsAsync(campaign.Id, toCreate.Count, ct);

            // ── 4. Tạo rows + đẩy job email queue ────────────────────────────
            var now = DateTime.UtcNow;
            var expiresAt = ResolveInvitationExpiry(campaign, now);

            // DB23 — token THÔ chỉ sống trong bộ nhớ (đi vào email/URL); DB lưu SHA-256(token).
            var invitations = toCreate.Select((email, index) =>
            {
                var rawToken = InvitationTokens.NewRawToken();
                return (RawToken: rawToken, Invitation: new CampaignInvitation
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaign.Id,
                    CampaignCandidateId = null,   // đường 1 (D1) — không gắn campaign_candidates
                    SlotId = assignedSlots[index],
                    TokenHash = InvitationTokens.Hash(rawToken),
                    Email = email,
                    ExpiresAt = expiresAt,
                    CreatedAt = now,
                });
            }).ToList();

            if (invitations.Count > 0)
            {
                _db.CampaignInvitations.AddRange(invitations.Select(x => x.Invitation));

                // DB2b — Transactional Outbox: ghi outbox-row CÙNG SaveChanges tạo invitation (thay
                // "publish best-effort SAU commit" cũ = dual-write mất mail khi broker down giữa 2 lần
                // SaveChanges). SentAt = "đã vào outbox" (dispatcher publish sau). Response giữ shape cũ.
                foreach (var (rawToken, invitation) in invitations)
                {
                    invitation.SentAt = now;
                    // Job mang token THÔ (email phải chứa link dùng được) — DB chỉ có hash.
                    _db.OutboxMessages.Add(OutboxMessage.ForInvitation(new InvitationEmailJob(
                        invitation.Id, campaign.Id, invitation.Email, rawToken, campaign.Title, invitation.ExpiresAt)));
                    response.Created.Add(new InvitationItem { Id = invitation.Id, Email = invitation.Email, ExpiresAt = invitation.ExpiresAt });
                }

                AddAudit(actorUserId, orgId, AuditAction.Invite, campaign.Id, $"Mời {invitations.Count} ứng viên qua email");
                await _db.SaveChangesAsync(ct);
            }

            return response;
        }

        // ── Danh sách lời mời đã phát (HR theo dõi phân phối) ───────────────────────────────
        // Bịt lỗ: `created[]` của POST chỉ sống trong 1 response, mà đường-1 (mời thẳng email) KHÔNG
        // sinh row cv_submission nên GET /candidates cũng không thấy → HR đóng tab là mất dấu đã mời ai,
        // và không lấy được invitationId để gọi reissue (D4).
        //
        // Trạng thái suy READ-TIME từ mốc thời gian (không thêm cột state phải đồng bộ). "Đã join" =
        // có row membership (D2) — KHÔNG dùng invitations.used_at vì cột đó chưa từng được ghi ở đâu.
        // FX1 — ghép membership theo QUAN HỆ THẬT `campaign_membership.invitation_id` (set lúc join, khi
        // token còn trong tay). Ghép theo cv_submission_id / email chỉ còn là fallback cho membership
        // LỊCH SỬ chưa có link — và chỉ áp cho row `invitation_id IS NULL`, để lời mời thứ hai cùng email
        // không còn "thơm lây" trạng thái Joined của lời mời thứ nhất.
        // ⚠ Membership tạo TRƯỚC F5 có Email = null và CvSubmissionId = null (đường-1 lịch sử) → không
        // ghép được → hiện "Sent"/"Expired" thay vì "Joined". Thà báo thiếu còn hơn đoán bừa.
        // Keyset-paged (DB8) theo (CreatedAt DESC, Id DESC) — đúng thứ tự vốn có nên không đổi UX.
        // ?search= lọc theo email (case-insensitive) và ?status= ĐỀU ĐẨY XUỐNG SQL: trạng thái tuy suy
        // read-time nhưng suy được từ đúng các cột đang có, nên diễn đạt lại thành vị ngữ SQL được.
        // Lọc trong C# sau khi phân trang sẽ cho kết quả SAI (chỉ lọc trong phạm vi 1 trang).
        public async Task<KeysetPage<InvitationListItem>> GetInvitationsAsync(
            Guid orgId, Guid id, string? status, string? search, string? cursor, int? limit, CancellationToken ct)
        {
            // Ownership: campaign phải của org (query filter loại soft-deleted) → không thấy = 404.
            var owns = await _db.Campaigns.AnyAsync(c => c.Id == id && c.OrgId == orgId, ct);
            if (!owns)
                throw new KeyNotFoundException($"Campaign {id} not found.");

            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);
            var now = DateTime.UtcNow;

            var q = _db.CampaignInvitations.Where(i => i.CampaignId == id);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLowerInvariant();
                q = q.Where(i => i.Email.ToLower().Contains(needle));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var want = NormalizeDeliveryStatus(status);
                if (want is null)
                    return KeysetPage<InvitationListItem>.Empty;   // giá trị lạ → rỗng (như hành vi cũ)

                // Chuỗi vị ngữ dưới đây là bản dịch 1-1 của thứ tự ưu tiên trong ResolveDeliveryStatus
                // (Revoked → Joined → Expired → Sent → Queued): mỗi bậc = "KHÔNG rơi vào bậc trên" +
                // điều kiện của chính nó. Lồng nhau (thay vì các `if` phẳng) để đúng cái "không rơi vào
                // bậc trên" đó không thể bị bỏ sót — nhờ vậy lời mời cũ sau reissue (D4) KHÔNG "thơm lây"
                // trạng thái Joined của lời mời MỚI cùng email.
                if (want == InvitationDeliveryStatus.Revoked)
                {
                    q = q.Where(i => i.RevokedAt != null);
                }
                else
                {
                    q = q.Where(i => i.RevokedAt == null);

                    if (want == InvitationDeliveryStatus.Joined)
                    {
                        q = q.Where(i => _db.CampaignMemberships.Any(m => m.CampaignId == id
                            && (m.InvitationId == i.Id
                                || (m.InvitationId == null
                                    && ((i.CampaignCandidateId != null && m.CvSubmissionId == i.CampaignCandidateId)
                                        || (m.Email != null && m.Email.Trim().ToLower() == i.Email.Trim().ToLower()))))));
                    }
                    else
                    {
                        q = q.Where(i => !_db.CampaignMemberships.Any(m => m.CampaignId == id
                            && (m.InvitationId == i.Id
                                || (m.InvitationId == null
                                    && ((i.CampaignCandidateId != null && m.CvSubmissionId == i.CampaignCandidateId)
                                        || (m.Email != null && m.Email.Trim().ToLower() == i.Email.Trim().ToLower()))))));

                        if (want == InvitationDeliveryStatus.Expired)
                            q = q.Where(i => i.ExpiresAt <= now);
                        else if (want == InvitationDeliveryStatus.Sent)
                            q = q.Where(i => i.ExpiresAt > now && i.EmailSentAt != null);
                        else   // Queued
                            q = q.Where(i => i.ExpiresAt > now && i.EmailSentAt == null);
                    }
                }
            }

            if (cur is not null)
                q = q.Where(i => i.CreatedAt < cur.CreatedAt
                    || (i.CreatedAt == cur.CreatedAt && i.Id.CompareTo(cur.Id) < 0));

            var invitations = await q
                .OrderByDescending(i => i.CreatedAt)
                .ThenByDescending(i => i.Id)
                .Take(take)
                .ToListAsync(ct);

            if (invitations.Count == 0)
                return KeysetPage<InvitationListItem>.Empty;

            // Membership chỉ nạp cho ĐÚNG TRANG (trước đây nạp cả campaign) — đủ để điền JoinedAt + suy
            // Status, mà không phải kéo toàn bộ bảng về chỉ để hiển thị ≤ limit dòng.
            var invIds = invitations.Select(i => i.Id).ToList();
            var cvIds = invitations.Where(i => i.CampaignCandidateId is not null)
                .Select(i => i.CampaignCandidateId!.Value).Distinct().ToList();
            var emails = invitations.Select(i => i.Email.Trim().ToLower()).Distinct().ToList();

            var memberships = await _db.CampaignMemberships
                .Where(m => m.CampaignId == id
                    && ((m.InvitationId != null && invIds.Contains(m.InvitationId.Value))
                        || (m.CvSubmissionId != null && cvIds.Contains(m.CvSubmissionId.Value))
                        || (m.Email != null && emails.Contains(m.Email.Trim().ToLower()))))
                .Select(m => new { m.InvitationId, m.CvSubmissionId, m.Email, m.JoinedAt })
                .ToListAsync(ct);

            // FX1 — ghép CHÍNH XÁC theo quan hệ membership.invitation_id trước. Hai nhánh cũ (cv_submission_id
            // rồi email) chỉ còn là FALLBACK cho membership LỊCH SỬ chưa có link (join trước FX1, và migration
            // cố ý không backfill khi không chắc). Membership ĐÃ có link thì KHÔNG được ghép bằng email nữa —
            // nếu không, lời mời thứ hai cùng email vẫn "thơm lây" trạng thái Joined của lời mời thứ nhất,
            // tức là đúng cái suy đoán mà quan hệ này sinh ra để bỏ.
            var joinedByInvitation = memberships
                .Where(m => m.InvitationId is not null)
                .GroupBy(m => m.InvitationId!.Value)
                .ToDictionary(g => g.Key, g => g.Max(m => m.JoinedAt));

            // Email so case-insensitive vì đường-1 chỉ Trim() còn đường-2 đã lowercase từ C13.
            var legacy = memberships.Where(m => m.InvitationId is null).ToList();
            var joinedByCv = legacy
                .Where(m => m.CvSubmissionId is not null)
                .GroupBy(m => m.CvSubmissionId!.Value)
                .ToDictionary(g => g.Key, g => g.Max(m => m.JoinedAt));
            var joinedByEmail = legacy
                .Where(m => !string.IsNullOrWhiteSpace(m.Email))
                .GroupBy(m => m.Email!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(m => m.JoinedAt), StringComparer.OrdinalIgnoreCase);

            var items = invitations.Select(i =>
            {
                var joined = joinedByInvitation.TryGetValue(i.Id, out var byInv)
                    ? (found: true, at: byInv)
                    : i.CampaignCandidateId is Guid ccid && joinedByCv.TryGetValue(ccid, out var byCv)
                    ? (found: true, at: byCv)
                    : joinedByEmail.TryGetValue(i.Email.Trim(), out var byEmail)
                        ? (found: true, at: byEmail)
                        : (found: false, at: (DateTime?)null);

                return new InvitationListItem
                {
                    Id = i.Id,
                    Email = i.Email,
                    // Vẫn suy bằng ResolveDeliveryStatus (NGUỒN DUY NHẤT của thứ tự ưu tiên) — vị ngữ SQL
                    // ở trên chỉ CHỌN dòng, không được phép tự định nghĩa lại trạng thái.
                    Status = ResolveDeliveryStatus(i, joined.found, now),
                    SentAt = i.SentAt,
                    EmailSentAt = i.EmailSentAt,
                    ExpiresAt = i.ExpiresAt,
                    RevokedAt = i.RevokedAt,
                    JoinedAt = joined.at,
                    CampaignCandidateId = i.CampaignCandidateId,
                    CreatedAt = i.CreatedAt
                };
            }).ToList();

            var next = invitations.Count == take
                ? new KeysetCursor(invitations[^1].CreatedAt, invitations[^1].Id).Encode()
                : null;

            return new KeysetPage<InvitationListItem>(items, next);
        }

        /// <summary>Chuẩn hoá `?status=` về đúng hằng trong <see cref="InvitationDeliveryStatus"/>; giá trị lạ → null.</summary>
        private static string? NormalizeDeliveryStatus(string status)
        {
            var s = status.Trim();
            foreach (var known in new[]
                     {
                         InvitationDeliveryStatus.Revoked, InvitationDeliveryStatus.Joined,
                         InvitationDeliveryStatus.Expired, InvitationDeliveryStatus.Sent,
                         InvitationDeliveryStatus.Queued
                     })
            {
                if (string.Equals(s, known, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return null;
        }

        // Thứ tự ưu tiên có chủ ý (xem InvitationDeliveryStatus): Revoked đứng trước Joined để lời mời
        // cũ sau reissue (D4) không hiện Joined nhờ lời mời MỚI cùng email.
        private static string ResolveDeliveryStatus(CampaignInvitation i, bool joined, DateTime now)
        {
            if (i.RevokedAt is not null) return InvitationDeliveryStatus.Revoked;
            if (joined) return InvitationDeliveryStatus.Joined;
            if (i.ExpiresAt <= now) return InvitationDeliveryStatus.Expired;
            if (i.EmailSentAt is not null) return InvitationDeliveryStatus.Sent;
            return InvitationDeliveryStatus.Queued;
        }

        // ── C15: Distribution đường 2 — mời hàng loạt từ shortlist sàng CV ──────────────────
        // HR chọn top sau ranking (candidateIds) → mỗi ứng viên: TÁCH EMAIL TỪ CV
        // (campaign_candidates.email, parse sẵn C13) → tạo invitation GẮN campaign_candidate_id +
        // đẩy email queue; Analyzed → Invited. Per-item best-effort (thiếu email / sai trạng thái → failed[],
        // KHÔNG chặn item khác); đã Invited → skip (absorbing). Vượt max_candidates → chặn CẢ request (400),
        // nhất quán D1. Campaign phải Active (không → 409); ngoài org → 404.
        public async Task<InviteShortlistResponse> InviteShortlistedCandidatesAsync(
            Guid orgId, Guid actorUserId, Guid id, List<Guid> candidateIds, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            if (campaign.Status != CampaignStatus.Active)
                throw new InvalidOperationException($"Chỉ mời ứng viên khi campaign đang Active (hiện: {campaign.Status}).");

            var response = new InviteShortlistResponse();
            var uniqueIds = (candidateIds ?? new List<Guid>()).Distinct().ToList();
            if (uniqueIds.Count == 0)
                return response;

            // Load ứng viên thuộc campaign này (ngoài campaign / không tồn tại → failed[]).
            var candidates = await _db.CvSubmissions
                .Where(c => c.CampaignId == id && uniqueIds.Contains(c.Id))
                .ToListAsync(ct);
            var byId = candidates.ToDictionary(c => c.Id);

            // Dedup email với invitation đã có (chưa revoke) của campaign — chống mời trùng qua 2 đường.
            var existingEmails = await _db.CampaignInvitations
                .Where(i => i.CampaignId == id && i.RevokedAt == null)
                .Select(i => i.Email)
                .ToListAsync(ct);
            var existingSet = new HashSet<string>(existingEmails, StringComparer.OrdinalIgnoreCase);

            var toInvite = new List<CvSubmission>();
            foreach (var cid in uniqueIds)
            {
                if (!byId.TryGetValue(cid, out var cand))
                {
                    response.Failed.Add(new FailedInviteItem { CandidateId = cid, Reason = "Không tìm thấy ứng viên trong campaign." });
                    continue;
                }

                // Absorbing: đã mời rồi → bỏ qua (không tạo invitation thứ 2, không lật trạng thái).
                if (cand.Status == CvSubmissionStatus.Invited)
                    continue;

                if (cand.Status != CvSubmissionStatus.Analyzed)
                {
                    response.Failed.Add(new FailedInviteItem { CandidateId = cid, Reason = $"Chỉ mời được ứng viên Analyzed (hiện: {cand.Status})." });
                    continue;
                }

                var email = cand.Email?.Trim();
                if (string.IsNullOrWhiteSpace(email))
                {
                    response.Failed.Add(new FailedInviteItem { CandidateId = cid, Reason = "Thiếu email — PATCH bổ sung rồi mời lại." });
                    continue;
                }

                if (!existingSet.Add(email))   // đã có invitation cho email này (đường 1 hoặc trùng batch)
                {
                    response.Failed.Add(new FailedInviteItem { CandidateId = cid, Reason = "Email đã được mời." });
                    continue;
                }

                toInvite.Add(cand);
            }

            // Cap max_candidates: invitation hiện có + mời mới ≤ cap → vượt = chặn CẢ request (như D1).
            if (toInvite.Count > 0)
                await EnsureCandidateCapacityAsync(orgId, campaign, existingEmails.Count, toInvite.Count, "lời mời", ct);

            if (toInvite.Count == 0)
                return response;

            // Tạo invitation (gắn campaign_candidate_id) + set Analyzed → Invited + outbox-row CÙNG tx.
            var now = DateTime.UtcNow;
            var expiresAt = ResolveInvitationExpiry(campaign, now);
            var assignedSlots = await AssignSlotsAsync(campaign.Id, toInvite.Count, ct);
            foreach (var (cand, index) in toInvite.Select((value, index) => (value, index)))
            {
                var rawToken = InvitationTokens.NewRawToken();   // DB23 — thô cho email, hash cho DB
                var invitation = new CampaignInvitation
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaign.Id,
                    CampaignCandidateId = cand.Id,   // đường 2 — gắn shortlist (đường 1 để null)
                    SlotId = assignedSlots[index],
                    TokenHash = InvitationTokens.Hash(rawToken),
                    Email = cand.Email!.Trim(),      // email đã chuẩn hoá lowercase từ C13/PATCH
                    ExpiresAt = expiresAt,
                    CreatedAt = now,
                    SentAt = now,                    // DB2b — "đã vào outbox" (dispatcher publish sau)
                };
                cand.Status = CvSubmissionStatus.Invited;
                cand.UpdatedAt = now;
                _db.CampaignInvitations.Add(invitation);

                // DB2b — outbox-row CÙNG SaveChanges tạo invitation (không dual-write mất mail khi broker down).
                _db.OutboxMessages.Add(OutboxMessage.ForInvitation(new InvitationEmailJob(
                    invitation.Id, campaign.Id, invitation.Email, rawToken, campaign.Title, invitation.ExpiresAt)));

                response.Invited.Add(new InvitedCandidateItem
                {
                    CandidateId = cand.Id,
                    InvitationId = invitation.Id,
                    Email = invitation.Email
                });
            }

            AddAudit(actorUserId, orgId, AuditAction.Invite, campaign.Id, $"Mời {toInvite.Count} ứng viên từ shortlist sàng CV");
            await _db.SaveChangesAsync(ct);
            return response;
        }

        // ── D4: phát lại lời mời (re-issue) — vô hiệu token cũ + phát token mới + resend email ──
        // Employer bấm "gửi lại" cho 1 lời mời (email nhập sai worker gửi, token lộ/hết hạn, HR huỷ+mời lại
        // theo CAMP-3). Vô hiệu token cũ (RevokedAt → GET/join token cũ = 410 qua ValidateInvitationUsable) +
        // tạo invitation MỚI cùng email (+ giữ campaign_candidate_id nếu là đường 2) với token mới + resend.
        // Idempotent: token cũ đã revoke → giữ mốc revoke cũ, VẪN tạo lời mời mới.
        // Ownership: campaign lọc theo org_id (ngoài org → 404); invitation không thuộc campaign → 404.
        // Campaign phải Active (Draft/Closed/Archived → 409) — nhất quán với mời hiện tại (CreateInvitations).
        // KHÔNG đụng membership (CampaignMembership, D2 — link mời chỉ để join) / cv_submission và KHÔNG đụng session.
        public async Task<InvitationItem> ReissueInvitationAsync(
            Guid orgId, Guid actorUserId, Guid id, Guid invitationId, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            var old = await _db.CampaignInvitations
                .FirstOrDefaultAsync(i => i.Id == invitationId && i.CampaignId == id, ct)
                ?? throw new KeyNotFoundException($"Invitation {invitationId} not found.");

            if (campaign.Status != CampaignStatus.Active)
                throw new InvalidOperationException($"Chỉ phát lại lời mời khi campaign đang Active (hiện: {campaign.Status}).");

            var now = DateTime.UtcNow;

            // Revoke token cũ (idempotent: đã revoke → giữ mốc cũ) + tạo invitation mới. Cả hai thay đổi
            // đi trong 1 SaveChangesAsync = 1 transaction (như CreateInvitationsAsync) → nguyên tử.
            old.RevokedAt ??= now;
            var slotId = old.SlotId;
            if (slotId is not null && !await _db.CampaignSlots.AnyAsync(s => s.Id == slotId && s.CampaignId == campaign.Id, ct))
                slotId = null;
            if (slotId is null)
                slotId = (await AssignSlotsAsync(campaign.Id, 1, ct))[0];

            var rawToken = InvitationTokens.NewRawToken();   // DB23 — thô cho email, hash cho DB
            var fresh = new CampaignInvitation
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                CampaignCandidateId = old.CampaignCandidateId,   // giữ liên kết shortlist (đường 2); đường 1 = null
                SlotId = slotId,
                TokenHash = InvitationTokens.Hash(rawToken),
                Email = old.Email,
                ExpiresAt = ResolveInvitationExpiry(campaign, now),
                CreatedAt = now,
                SentAt = now,                                    // DB2b — "đã vào outbox" (dispatcher publish sau)
            };
            _db.CampaignInvitations.Add(fresh);

            // DB2b — outbox-row CÙNG transaction (revoke token cũ + tạo fresh + outbox = 1 SaveChanges).
            // Thay "resend best-effort SAU commit" cũ (mất mail khi broker down) — dispatcher publish sau.
            _db.OutboxMessages.Add(OutboxMessage.ForInvitation(new InvitationEmailJob(
                fresh.Id, campaign.Id, fresh.Email, rawToken, campaign.Title, fresh.ExpiresAt)));

            AddAudit(actorUserId, orgId, AuditAction.ReissueInvitation, campaign.Id, $"Phát lại lời mời cho {old.Email}");
            await _db.SaveChangesAsync(ct);

            return new InvitationItem { Id = fresh.Id, Email = fresh.Email, ExpiresAt = fresh.ExpiresAt };
        }

        // ── E5: bảng kết quả + xếp hạng + pass/fail ─────────────────────────
        // Đọc read-model LOCAL `campaign_rankings` (E4 upsert từ event SessionScored) — không gọi
        // xuyên service. Bảng chỉ có 1 row/ứng viên đã `Scored` (row tạo khi nhận SessionScored) nên
        // "chỉ xếp hạng Scored" (CAMP-11) tự thoả — ứng viên chưa Scored không có row → không xuất hiện.
        // Ownership: chỉ chủ org (org_id) xem được — không phải chủ → 404 (KeyNotFoundException).
        public async Task<CampaignResultsResponse> GetCampaignResultsAsync(Guid orgId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // Materialize rồi sắp + gán rank TRONG BỘ NHỚ: EF Core không dịch ROW_NUMBER()/RANK() sang
            // LINQ; và trên SQLite (test) decimal lưu dạng TEXT → ORDER BY ở SQL có thể sai thứ tự số học.
            // Số row/1 campaign bị chặn bởi max_candidates (nhỏ) nên sort in-memory an toàn.
            var rows = await _db.CampaignRankings
                .Where(r => r.CampaignId == id)
                .ToListAsync(ct);

            // E11b — xếp hạng theo điểm EFFECTIVE (HR override ?? AI). Override đẩy ứng viên lên/xuống ranking.
            var ordered = rows
                .OrderByDescending(r => r.OverrideScore ?? r.TotalScore)
                .ThenBy(r => r.UpdatedAt)   // đồng điểm → ứng viên Scored sớm hơn đứng trước (tie-break ổn định)
                .ThenBy(r => r.SessionId)
                .ToList();

            // SEC-4 + R7: nạp TOÀN BỘ cờ của campaign 1 lần (read-model LOCAL session_flags, không xuyên
            // service) → gom theo buổi cho HR. Campaign không bật anti-cheat → không có cờ → [].
            var allFlags = await _db.SessionFlags
                .Where(f => f.CampaignId == id)
                .ToListAsync(ct);
            var flagsBySession = GroupFlagsBySession(allFlags);

            // F5: danh tính (tên/email) cho HR — 1 query, KHÔNG N+1 (mẫu GroupFlagsBySession).
            var identityByCandidate = await GetIdentityByCandidateAsync(id, ct);

            var threshold = campaign.PassScorePct;
            var results = new List<CampaignResultRow>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                var effectiveScore = r.OverrideScore ?? r.TotalScore;   // E11b

                // Đồng hạng (competition ranking): rank = số ứng viên điểm CAO HƠN + 1.
                // Đồng điểm (effective) → cùng rank; rank kế nhảy theo vị trí (1,1,3).
                int rank = (i > 0 && (ordered[i - 1].OverrideScore ?? ordered[i - 1].TotalScore) == effectiveScore)
                    ? results[i - 1].Rank
                    : i + 1;

                identityByCandidate.TryGetValue(r.CandidateId, out var identity);

                results.Add(new CampaignResultRow
                {
                    Rank = rank,
                    CandidateId = r.CandidateId,
                    FullName = identity.FullName,   // F5 — null nếu không tra được (default tuple = (null,null))
                    Email = identity.Email,
                    SessionId = r.SessionId,
                    TotalScore = effectiveScore,   // điểm effective (đã áp override); FE có AiScore để đối chiếu
                    // Pass/fail: HR override thắng ngưỡng; else so ngưỡng Employer (CAMP-11); ngưỡng null → null.
                    Result = r.OverrideResult
                        ?? (threshold is null ? null : (effectiveScore >= threshold.Value ? "Pass" : "Fail")),
                    ScoredAt = r.UpdatedAt,
                    Flags = flagsBySession.TryGetValue(r.SessionId, out var f) ? f : new List<FlagDto>(),
                    AiScore = r.TotalScore,
                    OverrideScore = r.OverrideScore,
                    OverrideResult = r.OverrideResult,
                    OverrideNote = r.OverrideNote,
                    OverriddenAt = r.OverriddenAt
                });
            }

            // R7: cờ của buổi CHƯA có row ranking (chưa Scored — bỏ ngang / đang thi) → không lọt vào `results`
            // ở trên. Gom riêng để HR vẫn thấy nhóm đáng ngờ nhất (SEC-4/D13). Nhiều cờ hơn = đáng ngờ hơn → lên
            // trước; tie-break session_id cho ổn định. Danh tính reuse identityByCandidate (F5, không thêm query).
            var scoredSessions = new HashSet<Guid>(ordered.Select(r => r.SessionId));
            var unscoredFlagged = allFlags
                .Where(f => !scoredSessions.Contains(f.SessionId))
                .GroupBy(f => f.SessionId)
                .Select(g =>
                {
                    var candidateId = g.Select(x => x.CandidateId).First();
                    identityByCandidate.TryGetValue(candidateId, out var identity);
                    return new UnscoredFlaggedRow
                    {
                        SessionId = g.Key,
                        CandidateId = candidateId,
                        FullName = identity.FullName,
                        Email = identity.Email,
                        Flags = flagsBySession.TryGetValue(g.Key, out var f) ? f : new List<FlagDto>()
                    };
                })
                .OrderByDescending(u => u.Flags.Sum(f => f.Count))
                .ThenBy(u => u.SessionId)
                .ToList();

            return new CampaignResultsResponse
            {
                CampaignId = id,
                PassScorePct = threshold,
                TotalCandidates = results.Count,
                Results = results,
                UnscoredFlagged = unscoredFlagged
            };
        }

        // E11b — HR chốt/sửa điểm-kết-quả cuối 1 ứng viên (điểm AI = gợi ý; D13 "AI = suggestion").
        // Ghi cột override trên campaign_rankings (Campaign-owned read-model); TotalScore AI giữ nguyên.
        // Clear (Score=null & Result=null) → về AI. Org-scoped (ngoài org → 404); audit mọi lần.
        public async Task OverrideResultAsync(
            Guid orgId, Guid actorUserId, Guid campaignId, Guid sessionId, OverrideResultRequest req, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var ranking = await _db.CampaignRankings
                .FirstOrDefaultAsync(r => r.SessionId == sessionId && r.CampaignId == campaignId, ct)
                ?? throw new KeyNotFoundException($"Ranking cho session {sessionId} không tồn tại (ứng viên chưa được chấm).");

            if (string.IsNullOrWhiteSpace(req.Note))
                throw new ArgumentException("Ghi chú (lý do điều chỉnh) là bắt buộc.");

            var result = string.IsNullOrWhiteSpace(req.Result) ? null : req.Result.Trim();
            if (result is not null && result != "Pass" && result != "Fail")
                throw new ArgumentException("Result chỉ nhận 'Pass' hoặc 'Fail'.");

            var isClear = req.Score is null && result is null;

            ranking.OverrideScore = req.Score;
            ranking.OverrideResult = result;
            ranking.OverrideNote = isClear ? null : req.Note.Trim();
            ranking.OverriddenBy = isClear ? null : actorUserId;
            ranking.OverriddenAt = isClear ? null : DateTime.UtcNow;

            AddAudit(actorUserId, orgId, AuditAction.OverrideResult, campaignId,
                isClear
                    ? $"Huỷ override session {sessionId} (về điểm AI). Lý do: {req.Note.Trim()}"
                    : $"Override session {sessionId}: score={req.Score?.ToString() ?? "—"}, result={result ?? "—"}. Lý do: {req.Note.Trim()}");
            await _db.SaveChangesAsync(ct);
        }

        // AI4 — HR đọc chi tiết transcript + nhận xét AI per-criterion + cờ needs_review của 1 buổi để đối
        // chiếu điểm ranking (E5). Gating GIỐNG OverrideResultAsync (E11b): org sở hữu campaign + ranking row
        // của sessionId thuộc campaign → ngoài org / session chưa được chấm = KeyNotFoundException (404).
        // Transcript OWNED bởi Interview (GEN-2 ref lỏng) → đọc xuyên-service qua internal client (X-Internal-
        // Token, không qua gateway). Client lỗi hạ tầng/non-success → DownstreamServiceException (502).
        public async Task<SessionTranscriptResponse> GetSessionTranscriptAsync(
            Guid orgId, Guid campaignId, Guid sessionId, CancellationToken ct)
        {
            _ = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            _ = await _db.CampaignRankings
                .FirstOrDefaultAsync(r => r.SessionId == sessionId && r.CampaignId == campaignId, ct)
                ?? throw new KeyNotFoundException($"Ranking cho session {sessionId} không tồn tại (ứng viên chưa được chấm).");

            if (_sessionClient is null)
                throw new InvalidOperationException("ICampaignSessionClient chưa được cấu hình.");

            return await _sessionClient.GetSessionTranscriptAsync(sessionId, ct);
        }

        // SEC-4: gom session_flags (đã materialize) của 1 campaign → Dictionary<session_id, List<FlagDto>>.
        // Group theo (session_id, signal_type) → count; Note = ghi chú non-empty đầu tiên (đại diện cho HR).
        // In-memory (số cờ/campaign nhỏ; tránh phụ thuộc dịch GROUP BY của provider). Caller nạp list 1 lần
        // (dùng chung cho cả bảng ranking lẫn nhóm R7 chưa-Scored) rồi truyền vào.
        private static Dictionary<Guid, List<FlagDto>> GroupFlagsBySession(IEnumerable<SessionFlag> flags)
        {
            return flags
                .GroupBy(f => f.SessionId)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.SignalType)
                          .OrderBy(t => t.Key, StringComparer.Ordinal)
                          .Select(t => new FlagDto
                          {
                              Type = t.Key,
                              Count = t.Count(),
                              Note = t.Select(x => x.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          })
                          .ToList());
        }

        // F5: tra danh tính ứng viên của 1 campaign → Dictionary<candidate_id, (full_name, email)>.
        // ĐÚNG 1 query cho cả bảng kết quả (mẫu GetFlagsBySessionAsync) — không N+1 theo từng dòng.
        // Nav `CvSubmission` là OPTIONAL nên EF dịch thành LEFT JOIN NGAY TRONG query này ⇒ vẫn 1 round-trip.
        // Fallback `?? m.CvSubmission.X`: che luôn membership đường-2 cũ mà backfill của migration sót
        // (và mọi row tạo trước F5 chưa join lại) → HR vẫn thấy tên/email thay vì ô trống.
        private async Task<Dictionary<Guid, (string? FullName, string? Email)>> GetIdentityByCandidateAsync(
            Guid campaignId, CancellationToken ct)
        {
            var rows = await _db.CampaignMemberships
                .Where(m => m.CampaignId == campaignId && m.CandidateId != null)
                .Select(m => new
                {
                    CandidateId = m.CandidateId!.Value,
                    FullName = m.FullName ?? (m.CvSubmission != null ? m.CvSubmission.FullName : null),
                    Email = m.Email ?? (m.CvSubmission != null ? m.CvSubmission.Email : null)
                })
                .ToListAsync(ct);

            // UNIQUE(campaign_id, candidate_id) ⇒ khoá không trùng; vẫn dùng nhóm-lấy-đầu cho an toàn
            // (dữ liệu cũ trước khi index unique được áp có thể còn trùng).
            return rows
                .GroupBy(x => x.CandidateId)
                .ToDictionary(g => g.Key, g => (g.First().FullName, g.First().Email));
        }

        // ── E6: xuất bảng kết quả (E5) ra file ──────────────────────────────
        // Tái dùng NGUYÊN VẸN GetCampaignResultsAsync (E5) → thứ tự + rank + pass/fail y hệt bảng web,
        // không tính lại (một nguồn sự thật). Ngoài org → E5 ném KeyNotFoundException → controller 404.
        // format: null/"" → mặc định csv; "csv" → csv; "pdf" → pdf (F16); khác → ArgumentException → 400.
        public async Task<CampaignResultExport> ExportCampaignResultsAsync(
            Guid orgId, Guid id, string? format, CancellationToken ct)
        {
            var normalized = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
            if (normalized != "csv" && normalized != "pdf")
                throw new ArgumentException($"format '{format}' không hợp lệ — chỉ hỗ trợ format=csv|pdf.");

            var results = await GetCampaignResultsAsync(orgId, id, ct);   // có thể ném KeyNotFoundException (404)

            if (normalized == "pdf")
            {
                // F16 — CÙNG object `results` với nhánh csv: hai bản xuất của một chiến dịch không được
                // phép lệch nhau, nên chỉ khác ở tầng serialize. Tiêu đề chỉ để trình bày; quyền sở hữu
                // đã kiểm ở GetCampaignResultsAsync ngay trên (ngoài org thì đã ném 404 rồi).
                var title = await _db.Campaigns
                    .Where(c => c.Id == id && c.OrgId == orgId)
                    .Select(c => c.Title)
                    .FirstOrDefaultAsync(ct) ?? "Chiến dịch";

                return new CampaignResultExport
                {
                    Content = CampaignResultsPdf.Build(results, title, DateTime.UtcNow),
                    ContentType = "application/pdf",
                    FileName = $"campaign_{id}_results.pdf"
                };
            }

            return new CampaignResultExport
            {
                Content = BuildResultsCsv(results),
                ContentType = "text/csv",
                FileName = $"campaign_{id}_results.csv"
            };
        }

        // ── C13: sàng CV hàng loạt — parse + archive PDF + hard-filter (rule cứng, 0 credit D18/D19) ──
        // Đồng bộ (chưa có AI queue — đó là C14). Mỗi CV: validate → parse text → tách email → dedup →
        // archive PDF gốc lên S3 (KEY, GEN-5) → hard-filter → Filtered | Rejected(reason). Trùng email → skip.
        // File hỏng/parse fail → Rejected (KHÔNG chặn cả batch). Vượt cap/thiếu file → 400. Chưa Active → 409.
        public async Task<ScreenCandidatesResponse> ScreenCandidatesAsync(
            Guid orgId, Guid actorUserId, Guid id, IFormFileCollection files, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // Guard: chỉ sàng khi Active (đã có campaign_criteria). Draft/Closed/Archived → 409.
            if (campaign.Status != CampaignStatus.Active)
                throw new InvalidOperationException($"Chỉ sàng CV khi campaign đang Active (hiện: {campaign.Status}).");

            if (files is null || files.Count == 0)
                throw new ArgumentException("Cần ít nhất 1 file CV (PDF).");

            // Cap số CV/campaign (chặn đốt AI vì free) — vượt → 400, chặn CẢ batch (như invitations).
            var currentCount = await _db.CvSubmissions.CountAsync(c => c.CampaignId == id, ct);
            await EnsureCandidateCapacityAsync(orgId, campaign, currentCount, files.Count, "CV", ct);

            // Dedup email: bộ đã có trong campaign + cộng dồn trong batch này (case-insensitive).
            var seenEmails = new HashSet<string>(
                await _db.CvSubmissions
                    .Where(c => c.CampaignId == id && c.Email != null)
                    .Select(c => c.Email!)
                    .ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            var response = new ScreenCandidatesResponse { Received = files.Count };
            var created = new List<CvSubmission>();
            var now = DateTime.UtcNow;

            foreach (var file in files)
            {
                var candidateId = Guid.NewGuid();

                // 1) Validate cơ bản (PDF, ≤10MB) — hỏng = Rejected (không chặn cả batch).
                if (file.Length == 0 || file.Length > MaxFileSizeBytes || !AllowedMimeTypes.Contains(file.ContentType))
                {
                    created.Add(NewRejectedCandidate(candidateId, campaign.Id, null, null, null,
                        "CV không hợp lệ (chỉ nhận PDF ≤ 10MB).", CvParseStatus.Failed, now));
                    continue;
                }

                // 2) Đọc bytes 1 lần → parse.
                byte[] buffer = await ReadFileBytesAsync(file);
                string? parsedText = null;
                try
                {
                    using var stream = new MemoryStream(buffer);
                    var result = await _parser.ParseAsync(stream, ct);
                    parsedText = result.RawText;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Parse CV thất bại (campaign {CampaignId}).", id);
                }

                // 3) Parse FAIL / text rỗng → Rejected; VẪN archive để HR xem file gốc.
                if (string.IsNullOrWhiteSpace(parsedText))
                {
                    var failKey = await ArchiveCvAsync(buffer, file, campaign.Id, candidateId, ct);
                    created.Add(NewRejectedCandidate(candidateId, campaign.Id, failKey, null, null,
                        "CV không đọc được — upload lại.", CvParseStatus.Failed, now));
                    continue;
                }

                // 4) Tách email từ CV (nguồn dedup + đường mời số 2).
                var email = ExtractEmail(parsedText);

                // 5) Dedup theo email → trùng thì BỎ QUA (không tạo row, không archive).
                if (email is not null && !seenEmails.Add(email))
                {
                    response.Skipped++;
                    continue;
                }

                // 6) Archive PDF gốc → S3 KEY (GEN-5). 7) Hard-filter → Filtered | Rejected(reason).
                var cvKey = await ArchiveCvAsync(buffer, file, campaign.Id, candidateId, ct);
                var reject = RunHardFilter(campaign, parsedText);

                created.Add(new CvSubmission
                {
                    Id = candidateId,
                    CampaignId = campaign.Id,
                    FullName = null,   // parse tên không đáng tin ở C13 → HR PATCH bổ sung (C14)
                    Email = email,
                    CvFileUrl = cvKey,
                    CvParsedText = parsedText,
                    ParseStatus = CvParseStatus.Done,
                    Status = reject is null ? CvSubmissionStatus.Filtered : CvSubmissionStatus.Rejected,
                    RejectReason = reject,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            if (created.Count > 0)
            {
                _db.CvSubmissions.AddRange(created);
                AddAudit(actorUserId, orgId, AuditAction.ScreenCandidates, campaign.Id,
                    $"Sàng {response.Received} CV: {created.Count(c => c.Status == CvSubmissionStatus.Filtered)} qua, " +
                    $"{created.Count(c => c.Status == CvSubmissionStatus.Rejected)} loại, {response.Skipped} trùng");
                await _db.SaveChangesAsync(ct);
            }

            response.Rejected = created.Count(c => c.Status == CvSubmissionStatus.Rejected);
            response.Filtered = created.Count(c => c.Status == CvSubmissionStatus.Filtered);
            response.Candidates = created.Select(c => new ScreenedCandidateItem
            {
                Id = c.Id,
                FullName = c.FullName,
                Email = c.Email,
                Status = c.Status.ToString(),
                RejectReason = c.RejectReason
            }).ToList();

            return response;
        }

        // C13: serve CV gốc (PDF) cho HR. Ownership qua campaign.org_id (join) → ngoài org = 404.
        // cv_file_url null (chưa archive) → FileNotFoundException (404).
        public async Task<Stream> DownloadCandidateCvAsync(Guid orgId, Guid id, Guid candidateId, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(
                    c => c.Id == candidateId && c.CampaignId == id && c.Campaign.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            if (string.IsNullOrWhiteSpace(candidate.CvFileUrl))
                throw new FileNotFoundException($"No CV file archived for candidate {candidateId}.");

            return await _file.DownloadAsync(candidate.CvFileUrl, ct);
        }

        // C13: archive PDF gốc → S3, trả về KEY (GEN-5: lưu key không full URL). Key deterministic theo candidate.
        private async Task<string> ArchiveCvAsync(byte[] buffer, IFormFile file, Guid campaignId, Guid candidateId, CancellationToken ct)
        {
            var key = $"campaigns/{campaignId}/candidates/{candidateId}.pdf";
            using var uploadStream = new MemoryStream(buffer);
            await _file.UploadAsync(new FormFile(uploadStream, 0, buffer.Length, file.Name, file.FileName)
            {
                Headers = file.Headers,
                ContentType = file.ContentType
            }, key, ct);
            return key;
        }

        // C13: hard-filter (rule cứng, 0 cost AI) — null = qua (Filtered); string = lý do loại (Rejected).
        // Thứ tự: required_skills (phải ĐỦ) → keywords_any (≥1) → min_years_experience.
        private static string? RunHardFilter(Campaign campaign, string cvText)
        {
            if (campaign.RequiredSkills is { Count: > 0 })
            {
                var missing = campaign.RequiredSkills
                    .Where(s => !string.IsNullOrWhiteSpace(s) && !CvContains(cvText, s))
                    .ToList();
                if (missing.Count > 0)
                    return $"Thiếu kỹ năng bắt buộc: {string.Join(", ", missing)}.";
            }

            if (campaign.KeywordsAny is { Count: > 0 })
            {
                var hasAny = campaign.KeywordsAny.Any(k => !string.IsNullOrWhiteSpace(k) && CvContains(cvText, k));
                if (!hasAny)
                    return $"Không có từ khóa nào trong: {string.Join(", ", campaign.KeywordsAny)}.";
            }

            // min_years: CHỈ loại khi parse được số năm & < ngưỡng (doc: không chắc thì nhường AI — không loại oan).
            if (campaign.MinYearsExperience is int min && min > 0)
            {
                var years = TryExtractYears(cvText);
                if (years is int y && y < min)
                    return $"Kinh nghiệm {y} năm < yêu cầu {min} năm.";
            }

            return null;
        }

        // Match kỹ năng/từ khóa = substring case-insensitive (skill có ký tự đặc biệt: C#, ASP.NET → không dùng word-boundary).
        private static bool CvContains(string cvText, string term)
            => cvText.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);

        private static readonly Regex EmailRegex =
            new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

        // C13: tách email đầu tiên từ CV (chuẩn hoá lowercase để dedup ổn định). Không có → null.
        private static string? ExtractEmail(string text)
        {
            var m = EmailRegex.Match(text);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        private static readonly Regex YearsRegex =
            new(@"(\d{1,2})\s*\+?\s*(?:years|year|năm)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // C13: heuristic thô "X years"/"X năm" → số lớn nhất. Không thấy → null (doc: parse năm KN không chắc).
        private static int? TryExtractYears(string text)
        {
            int? max = null;
            foreach (Match m in YearsRegex.Matches(text))
                if (int.TryParse(m.Groups[1].Value, out var y))
                    max = max is null ? y : Math.Max(max.Value, y);
            return max;
        }

        // C13: row ứng viên bị loại (file hỏng / parse fail). Id/CreatedAt set sẵn (chạy SQLite test + Postgres).
        private static CvSubmission NewRejectedCandidate(
            Guid id, Guid campaignId, string? cvKey, string? email, string? parsedText,
            string reason, CvParseStatus parseStatus, DateTime now)
            => new()
            {
                Id = id,
                CampaignId = campaignId,
                CvFileUrl = cvKey,
                Email = email,
                CvParsedText = parsedText,
                ParseStatus = parseStatus,
                Status = CvSubmissionStatus.Rejected,
                RejectReason = reason,
                CreatedAt = now,
                UpdatedAt = now
            };

        // Serialize bảng kết quả → CSV bằng CsvHelper (tự escape comma/quote/newline — không tự nối chuỗi).
        // Cột snake_case (§5): rank,candidate_id,session_id,total_score,result,scored_at,flags,full_name,email.
        private static byte[] BuildResultsCsv(CampaignResultsResponse results)
        {
            var rows = results.Results.Select(r => new ResultCsvRow
            {
                Rank = r.Rank,
                CandidateId = r.CandidateId,
                SessionId = r.SessionId,
                TotalScore = r.TotalScore,
                Result = r.Result ?? string.Empty,   // ngưỡng null → ô result rỗng (HR quyết tay)
                ScoredAt = r.ScoredAt,
                // SEC-4: tóm tắt cờ chống gian lận "type:count" ngăn bởi "; " (rỗng nếu không có cờ) cho HR đọc.
                Flags = string.Join("; ", r.Flags.Select(f => $"{f.Type}:{f.Count}")),
                // F5: null → ô rỗng (không tra được danh tính) — CsvHelper tự escape dấu phẩy/nháy trong tên.
                FullName = r.FullName ?? string.Empty,
                Email = r.Email ?? string.Empty
            }).ToList();

            // R7: nối ứng viên có cờ mà CHƯA Scored — HR đọc bản export cũng thấy nhóm đáng ngờ nhất.
            // rank/total_score/scored_at để TRỐNG (nullable → CsvHelper ghi ô rỗng); result = "Chưa chấm".
            rows.AddRange(results.UnscoredFlagged.Select(u => new ResultCsvRow
            {
                Rank = null,
                CandidateId = u.CandidateId,
                SessionId = u.SessionId,
                TotalScore = null,
                Result = "Chưa chấm",
                ScoredAt = null,
                Flags = string.Join("; ", u.Flags.Select(f => $"{f.Type}:{f.Count}")),
                FullName = u.FullName ?? string.Empty,
                Email = u.Email ?? string.Empty
            }));

            using var buffer = new MemoryStream();
            // leaveOpen: StreamWriter/CsvWriter dispose (flush) nhưng KHÔNG đóng buffer → ToArray() sau đó đọc được.
            using (var writer = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                csv.Context.RegisterClassMap<ResultCsvRowMap>();
                csv.WriteRecords(rows);   // list rỗng → vẫn ghi hàng header (theo map)
            }
            return buffer.ToArray();
        }

        // Model dòng CSV — tách khỏi DTO API để kiểm soát header + định dạng (scoped nội bộ E6).
        private sealed class ResultCsvRow
        {
            // R7: nullable → dòng "chưa Scored" ghi ô TRỐNG cho rank/điểm/scored_at (CsvHelper: null → "").
            public int? Rank { get; set; }
            public Guid CandidateId { get; set; }
            public Guid SessionId { get; set; }
            public decimal? TotalScore { get; set; }
            public string Result { get; set; } = string.Empty;
            public DateTime? ScoredAt { get; set; }
            public string Flags { get; set; } = string.Empty;   // SEC-4: tóm tắt cờ chống gian lận
            public string FullName { get; set; } = string.Empty;   // F5
            public string Email { get; set; } = string.Empty;      // F5
        }

        private sealed class ResultCsvRowMap : ClassMap<ResultCsvRow>
        {
            public ResultCsvRowMap()
            {
                Map(m => m.Rank).Index(0).Name("rank");
                Map(m => m.CandidateId).Index(1).Name("candidate_id");
                Map(m => m.SessionId).Index(2).Name("session_id");
                Map(m => m.TotalScore).Index(3).Name("total_score").TypeConverterOption.Format("0.##");
                Map(m => m.Result).Index(4).Name("result");
                // ScoredAt là UTC (UpdatedAt server) → ISO 8601 với hậu tố Z (chữ hoa = literal).
                Map(m => m.ScoredAt).Index(5).Name("scored_at").TypeConverterOption.Format("yyyy-MM-ddTHH:mm:ssZ");
                Map(m => m.Flags).Index(6).Name("flags");   // SEC-4: cột cờ chống gian lận (rỗng nếu không có)
                // F5 — danh tính CUỐI bảng: Index(n) là TUYỆT ĐỐI, chèn vào giữa sẽ phải đánh số lại toàn bộ
                // và đổi thứ tự cột của file HR đang dùng. Thêm ở đuôi = additive, script cũ không vỡ.
                Map(m => m.FullName).Index(7).Name("full_name");
                Map(m => m.Email).Index(8).Name("email");
            }
        }

        // E5: ngưỡng pass/fail là % điểm tổng → phải ∈ [0,100] khi có (null = HR quyết tay).
        private string ValidateLanguage(string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return "vi";
            var language = requested.Trim().ToLowerInvariant();
            if (language is not ("vi" or "en"))
                throw new ArgumentException("language chỉ nhận vi hoặc en.");
            if (!_bilingualEnabled && language != "vi")
                throw new ArgumentException("Bilingual campaign chưa được bật.");
            return language;
        }

        private static void ValidatePassScorePct(int? pct)
        {
            if (pct is int p && (p < 0 || p > 100))
                throw new ArgumentException($"pass_score_pct phải trong khoảng [0, 100] (hiện: {p}).");
        }

        // INT-17: trần câu thích ứng — null = dùng mặc định phía Interview; có giá trị thì phải ≥ 0
        // (0 = không thêm câu nào). Khớp CHECK ck_campaigns_adaptive_caps_non_negative ở DB.
        /// <summary>
        /// Trần số câu tối đa cho 1 buổi phỏng vấn — PHẢI khớp CHECK
        /// <c>ck_practice_sessions_max_questions_range</c> bên InterviewService (task F2b).
        /// </summary>
        private const int MaxQuestionsPerSession = 20;

        /// <summary>
        /// INT-17b — trần số câu ĐÀO SÂU cho MỖI câu campaign. Độ dài bài nhân lên theo
        /// <c>số câu campaign × (1 + trần)</c>, mà mỗi câu trả lời lại cõng thêm một lượt gọi AI ĐỒNG BỘ
        /// ⇒ đặt trần thấp là cố ý: 3 tầng trên campaign 10 câu đã là bài 40 câu.
        /// </summary>
        private const int MaxDeepPerQuestionCap = 3;

        /// <summary>
        /// Trần số câu thích ứng thêm cho CẢ buổi. Trước INT-17b field này KHÔNG có trần trên nào —
        /// HR gõ 50 là qua sạch. Vá luôn ở đây vì chế độ chuỗi làm hậu quả nặng hơn hẳn.
        /// </summary>
        private const int MaxFollowUpsCap = MaxQuestionsPerSession;

        // Trần thi đồng thời PHẢI >= 1. Guard bên ParticipationService là `running >= max`, nên với
        // 0 hoặc số âm thì `0 >= 0` / `0 >= -1` đều đúng ngay từ ứng viên ĐẦU TIÊN ⇒ mọi lượt Start
        // trả 429 và chiến dịch bị khoá vĩnh viễn mà HR không hiểu vì sao. Chặn ở đây — nơi HR nhập —
        // đúng bài học F2b: đừng để giá trị vô lý lọt xuống tận lúc ứng viên bấm Start.
        // KHÔNG đặt trần trên: số lớn = "không giới hạn", vô hại (khác max_questions vốn có CHECK ở DB).
        private static void ValidateConcurrencyCap(int? maxConcurrentInterviews)
        {
            if (maxConcurrentInterviews is int c && c < 1)
                throw new ArgumentException(
                    $"max_concurrent_interviews phải >= 1 (hiện: {c}). Bỏ trống = không giới hạn.");
        }

        private static void ValidateAdaptiveCaps(int? maxFollowUps, int? maxQuestions, int? maxDeepPerQuestion = null)
        {
            if (maxFollowUps is int f && f < 0)
                throw new ArgumentException($"max_follow_ups không được âm (hiện: {f}).");
            if (maxQuestions is int q && q < 0)
                throw new ArgumentException($"max_questions không được âm (hiện: {q}).");
            if (maxDeepPerQuestion is int d && d < 0)
                throw new ArgumentException($"max_deep_per_question không được âm (hiện: {d}).");

            if (maxFollowUps is int mf && mf > MaxFollowUpsCap)
                throw new ArgumentException(
                    $"max_follow_ups tối đa {MaxFollowUpsCap} (hiện: {mf}).");

            if (maxDeepPerQuestion is int md && md > MaxDeepPerQuestionCap)
                throw new ArgumentException(
                    $"max_deep_per_question tối đa {MaxDeepPerQuestionCap} (hiện: {md}).");

            // F2b — chặn trần Ở ĐÂY, nơi HR nhập, chứ không để lọt xuống lúc ứng viên bấm Start.
            // Trước đây guard chỉ chặn số âm ⇒ HR đặt max_questions=100000 qua sạch. Từ F2b có CHECK
            // `max_questions BETWEEN 0 AND 20` trên practice_sessions, nên giá trị đó sẽ ném lúc INSERT
            // session — tức là SAU khi đã reserve credit của org (PAY-6) ⇒ vừa hỏng đường doanh thu B2B
            // vừa để lại reservation mồ côi, mà lỗi lại nổ ở service KHÁC với chỗ nhập sai.
            // InterviewService có clamp phòng thủ, nhưng clamp là lưới an toàn: HR nhập 100000 mà hệ thống
            // lặng lẽ chạy 20 là sai kiểu khác. 400 ngay lúc tạo campaign mới là phản hồi đúng.
            if (maxQuestions is int mq && mq > MaxQuestionsPerSession)
                throw new ArgumentException(
                    $"max_questions tối đa {MaxQuestionsPerSession} (hiện: {mq}).");
        }

        private async Task<CampaignEntitlement> ResolveEntitlementAsync(Guid orgId, CancellationToken ct)
            => _entitlements is null
                ? CampaignEntitlement.Starter
                : await _entitlements.ResolveOrgAsync(orgId, ct);

        private async Task EnsureCanCreateCampaignAsync(Guid orgId, CampaignEntitlement entitlement, CancellationToken ct)
        {
            var active = await _db.Campaigns.CountAsync(c => c.OrgId == orgId && c.Status == CampaignStatus.Active, ct);
            if (active >= entitlement.MaxActiveCampaigns)
                throw new EntitlementForbiddenException(
                    $"Gói {entitlement.TierCode} chỉ cho phép {entitlement.MaxActiveCampaigns} campaign Active; hiện có {active}.");
        }

        private static void ValidateEntitledSelection(
            int? maxCandidates, bool adaptiveEnabled, bool groundingEnabled, CampaignEntitlement entitlement)
        {
            if (maxCandidates is > 0 && maxCandidates > entitlement.MaxCandidatesCap)
                throw new ArgumentException($"maxCandidates vượt trần {entitlement.MaxCandidatesCap} của gói {entitlement.TierCode}.");
            if (adaptiveEnabled && !entitlement.AdaptiveEnabled)
                throw new EntitlementForbiddenException($"Gói {entitlement.TierCode} không hỗ trợ adaptive interview.");
            if (groundingEnabled && !entitlement.GroundingEnabled)
                throw new EntitlementForbiddenException($"Gói {entitlement.TierCode} không hỗ trợ grounding.");
        }

        // Only requested mutations are gated. A tier expiry must not evict or freeze an existing campaign.
        private static void ValidateEntitledMutation(
            int? maxCandidates, bool? adaptiveEnabled, bool? groundingEnabled, CampaignEntitlement entitlement)
        {
            if (maxCandidates.HasValue && maxCandidates.Value > entitlement.MaxCandidatesCap)
                throw new ArgumentException($"maxCandidates vượt trần {entitlement.MaxCandidatesCap} của gói {entitlement.TierCode}.");
            if (adaptiveEnabled == true && !entitlement.AdaptiveEnabled)
                throw new EntitlementForbiddenException($"Gói {entitlement.TierCode} không hỗ trợ adaptive interview.");
            if (groundingEnabled == true && !entitlement.GroundingEnabled)
                throw new EntitlementForbiddenException($"Gói {entitlement.TierCode} không hỗ trợ grounding.");
        }

        private async Task EnsureCandidateCapacityAsync(
            Guid orgId, Campaign campaign, int currentCount, int batchCount, string label, CancellationToken ct)
        {
            var entitlement = await ResolveEntitlementAsync(orgId, ct);
            var effectiveCap = campaign.MaxCandidates is int campaignCap
                ? Math.Min(campaignCap, entitlement.MaxCandidatesCap)
                : entitlement.MaxCandidatesCap;
            if (currentCount + batchCount > effectiveCap)
                throw new ArgumentException(
                    $"Vượt giới hạn {label} hiệu lực ({effectiveCap}): hiện có {currentCount}, đang thêm {batchCount}.");
        }

        // Slot null means the campaign has no schedule configured, so legacy invitations remain unrestricted.
        // For configured slots, count invitation rows (not distinct email) and reserve capacity for this batch
        // in-memory so a single request is evenly distributed and all-or-nothing.
        private async Task<List<Guid?>> AssignSlotsAsync(Guid campaignId, int count, CancellationToken ct)
        {
            if (count == 0) return [];
            var slots = await _db.CampaignSlots.Where(s => s.CampaignId == campaignId)
                .OrderBy(s => s.StartsAt).ThenBy(s => s.Id).ToListAsync(ct);
            if (slots.Count == 0) return Enumerable.Repeat<Guid?>(null, count).ToList();
            var used = await _db.CampaignInvitations.Where(i => i.CampaignId == campaignId && i.RevokedAt == null && i.SlotId != null)
                .GroupBy(i => i.SlotId!.Value).Select(g => new { SlotId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.SlotId, x => x.Count, ct);
            var result = new List<Guid?>(count);
            for (var i = 0; i < count; i++)
            {
                var selected = slots.Where(s => (used.TryGetValue(s.Id, out var n) ? n : 0) < s.Capacity)
                    .OrderBy(s => used.TryGetValue(s.Id, out var n) ? n : 0).ThenBy(s => s.StartsAt).ThenBy(s => s.Id).FirstOrDefault();
                if (selected is null) throw new ArgumentException("Tất cả khung giờ đã đủ ứng viên được mời.");
                used[selected.Id] = (used.TryGetValue(selected.Id, out var existing) ? existing : 0) + 1;
                result.Add(selected.Id);
            }
            return result;
        }

        // DB23 — hạn token: campaign có deadline → dùng deadline (giữ ràng buộc token ≤ hạn campaign);
        // KHÔNG có deadline → now + Invitation:DefaultExpiryDays. Trước đây nhánh này để NULL = token
        // sống vĩnh viễn (magic-link redeem được mãi mãi).
        // (Sinh token đã chuyển sang InvitationTokens.NewRawToken — DB lưu hash, không lưu thô.)
        private DateTime ResolveInvitationExpiry(Campaign campaign, DateTime now)
        {
            if (campaign.ExpiresAt is DateTime deadline)
                return deadline;

            var days = Math.Max(1, _invitationSettings.DefaultExpiryDays);   // cấu hình ≤0 → 1 ngày, không bao giờ vô hạn
            return now.AddDays(days);
        }

        // C12: validate + build tiêu chí structured HR khai thẳng (source=HrEdited).
        // Ràng buộc (hỏng → ArgumentException → 400): ≥1 tiêu chí · name non-empty + không trùng (case-insensitive)
        // · 0 < weight ≤ 1 · maxScore ≥ 1 · Σweight ∈ [0.99, 1.01]. Trong khoảng → chuẩn hoá Σ→1.
        // order_no đánh theo thứ tự gửi lên (0-based).
        private static List<CampaignCriterion> BuildStructuredCriteria(Guid campaignId, List<CriterionItem> items)
        {
            if (items is null || items.Count == 0)
                throw new ArgumentException("criteria[] phải có ≥1 tiêu chí.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleaned = new List<(string Name, string? Description, decimal Weight, int MaxScore)>();
            foreach (var item in items)
            {
                var name = item.Name?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Tên tiêu chí không được rỗng.");
                if (!seen.Add(name))
                    throw new ArgumentException($"Tên tiêu chí bị trùng: '{name}'.");
                if (item.Weight <= 0m || item.Weight > 1m)
                    throw new ArgumentException($"weight của '{name}' phải trong khoảng (0, 1] (hiện: {item.Weight}).");
                if (item.MaxScore < 1)
                    throw new ArgumentException($"maxScore của '{name}' phải ≥ 1 (hiện: {item.MaxScore}).");

                cleaned.Add((name,
                    string.IsNullOrWhiteSpace(item.Description) ? null : item.Description!.Trim(),
                    item.Weight, item.MaxScore));
            }

            var total = cleaned.Sum(c => c.Weight);
            if (total < 0.99m || total > 1.01m)
                throw new ArgumentException(
                    $"Σweight phải trong khoảng [0.99, 1.01] để chuẩn hoá về 1 (hiện: {total}).");

            var now = DateTime.UtcNow;
            var criteria = cleaned.Select((c, i) => new CampaignCriterion
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                OrderNo = i,                          // 0-based theo thứ tự gửi lên
                Name = c.Name,
                Description = c.Description,
                Weight = Math.Round(c.Weight / total, 4),   // chuẩn hoá Σ→1
                MaxScore = c.MaxScore,
                Source = CriterionSource.HrEdited,
                CreatedAt = now,
                UpdatedAt = now
            }).ToList();

            // Sửa sai số làm tròn → Σ = 1 tuyệt đối (dồn vào tiêu chí đầu, như nhánh AI).
            criteria[0].Weight += 1m - criteria.Sum(c => c.Weight);
            return criteria;
        }

        // C8: gọi AIService đề xuất tiêu chí; lỗi/rỗng → fallback default. Chuẩn hoá Σweight = 1.
        private async Task<List<CampaignCriterion>> BuildCriteriaAsync(Campaign campaign, CancellationToken ct)
        {
            var jobCategory = string.IsNullOrWhiteSpace(campaign.Domain) ? "BE" : campaign.Domain!;
            var suggested = await _suggester.SuggestAsync(jobCategory, campaign.JDText, campaign.CriteriaText, 4, campaign.Language, ct);

            if (suggested is not { Count: > 0 })
                return BuildDefaultCriteria(campaign.Id);   // fallback khi AI lỗi/rỗng

            var total = suggested.Sum(s => s.Weight);
            if (total <= 0) total = 1m;
            var now = DateTime.UtcNow;
            var criteria = suggested.Select((s, i) => new CampaignCriterion
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                OrderNo = i,
                Name = s.Name,
                Description = s.Description,
                Weight = Math.Round(s.Weight / total, 4),
                MaxScore = s.MaxScore <= 0 ? 5 : s.MaxScore,
                Source = CriterionSource.AiSuggested,
                CreatedAt = now,
                UpdatedAt = now
            }).ToList();
            criteria[0].Weight += 1m - criteria.Sum(c => c.Weight);   // sửa sai số làm tròn → Σ=1
            return criteria;
        }

        // Fallback khi AIService không khả dụng — Σweight = 1 (0.4+0.3+0.3). Id/CreatedAt/UpdatedAt/OrderNo set sẵn.
        private static List<CampaignCriterion> BuildDefaultCriteria(Guid campaignId)
        {
            var now = DateTime.UtcNow;
            return new()
            {
                new() { Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = 0, Name = "Kiến thức chuyên môn", Weight = 0.4m, MaxScore = 5, Source = CriterionSource.AiSuggested, CreatedAt = now, UpdatedAt = now },
                new() { Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = 1, Name = "Giao tiếp / trình bày", Weight = 0.3m, MaxScore = 5, Source = CriterionSource.AiSuggested, CreatedAt = now, UpdatedAt = now },
                new() { Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = 2, Name = "Giải quyết vấn đề",     Weight = 0.3m, MaxScore = 5, Source = CriterionSource.AiSuggested, CreatedAt = now, UpdatedAt = now },
            };
        }

        // C10/BK4: ghi vết thao tác. actor_user_id = cá nhân HR thao tác (user sub, giữ danh tính người);
        // org_id = ORG sở hữu campaign (ownership context). Id/At set sẵn (chạy SQLite test + Postgres).
        private void AddAudit(Guid actorUserId, Guid orgId, AuditAction action, Guid entityId, string? summary)
            => _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                ActorUserId = actorUserId,
                Action = action,
                Entity = "Campaign",
                EntityId = entityId,
                Summary = summary,
                At = DateTime.UtcNow
            });

        // Nhãn field trong thông báo lỗi 400 — để người dùng biết CHỖ NÀO quá dài (JD hay tiêu chí).
        private const string JdTextLabel = "Mô tả công việc (jdText)";
        private const string CriteriaTextLabel = "Tiêu chí (criteriaText)";

        // C11: chuẩn hoá JD/Criteria nhập trực tiếp — trim; rỗng/whitespace → null.
        // + cap độ dài (TextInputLimits.JdTextMaxChars — ngưỡng CHUNG với B2C/Interview): text nhập tay đi
        // thẳng vào prompt Gemini → vượt ngưỡng ném ArgumentException (controller map → 400) kèm giới hạn
        // và độ dài đang gửi. Đo SAU khi trim → khoảng trắng thừa không tính vào ngưỡng.
        private static string? NormalizeText(string? text, string fieldLabel)
            => TextInputLimits.NormalizeAndEnsureLimit(
                text, fieldLabel, msg => new ArgumentException(msg));

        // C11: slot đã nhập JD/Criteria dạng TEXT trực tiếp (text có + chưa gắn file) → text ưu tiên, bỏ file.
        // (Có file_url = nguồn từ PDF trước đó → cho phép thay bằng file mới, không coi là "text trực tiếp".)
        private static bool HasDirectText(string? text, string? fileUrl)
            => !string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(fileUrl);

        private static void ValidateFile(IFormFile file, string label)
        {
            if (file.Length == 0)
                throw new ArgumentException($"{label} file is empty.");

            if (file.Length > MaxFileSizeBytes)
                throw new ArgumentException($"{label} file exceeds the 10 MB limit.");

            if (!AllowedMimeTypes.Contains(file.ContentType))
                throw new ArgumentException(
                    $"{label} file type '{file.ContentType}' is not allowed. " +
                    "Only PDF is accepted.");
        }

        private static async Task<byte[]> ReadFileBytesAsync(IFormFile file)
        {
            await using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        
        private async Task<(string Label, string Url, string? Text)?> HandleFileAsync(IFormFile? file, Guid campaignId, string label, CancellationToken ct)
        {
            if (file is null) return null;

            // Read once into memory
            byte[] buffer = await ReadFileBytesAsync(file);

            string? parsedText = null;
            if (file.ContentType == "application/pdf")
            {
                using var stream = new MemoryStream(buffer);
                var result = await _parser.ParseAsync(stream, ct);
                parsedText = result.RawText;
            }

            // Upload using buffer (avoid reopening stream twice)
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var path = $"campaigns/{campaignId}/{label}{ext}";
            using var uploadStream = new MemoryStream(buffer);
            await _file.UploadAsync(new FormFile(uploadStream, 0, buffer.Length, file.Name, file.FileName)
            {
                Headers = file.Headers,
                ContentType = file.ContentType
            }, path, ct);

            // Lưu KEY (path) trong DB — download/delete dùng key; URL ghép khi cần (bug #1)
            return (label, path, parsedText);
        }
    }
}
