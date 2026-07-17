using Amazon.S3.Model;
using CsvHelper;
using CsvHelper.Configuration;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
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
        private static readonly HashSet<string> AllowedMimeTypes = new()
            {
                "application/pdf",
            };
        private const long MaxFileSizeBytes = 10 * 1024 * 1024;

        public CampaignService(CampaignDbContext db,
            IFileService file, ILogger<CampaignService> logger,
            IParserService parser, ICriteriaSuggester suggester,
            IInvitationEmailPublisher emailPublisher,
            ICampaignSessionClient? sessionClient = null)
        {
            _db = db;
            _file = file;
            _logger = logger;
            _parser = parser;
            _suggester = suggester;
            _emailPublisher = emailPublisher;
            _sessionClient = sessionClient;
        }

        public async Task<CampaignResponse> CreateCampaignAsync(Guid orgId, Guid actorUserId, CreateCampaignRequest request, CancellationToken ct = default)
        {
            // ── 1. Validate questions ───────────────────────────
            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                throw new ArgumentException("All questions must have non-empty text.");

            ValidatePassScorePct(request.PassScorePct);   // E5: ngưỡng ∈ [0,100] nếu có

            // ── 2. Build campaign entity ────────────────────────
            var campaign = new Campaign
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                Title = request.Title,
                Domain = request.Domain,
                Status = CampaignStatus.Draft,
                MaxCandidates = request.MaxCandidates,
                TimeLimitMinutes = request.TimeLimitMinutes,
                AntiCheatEnabled = request.AntiCheatEnabled,
                FaceVerifyEnabled = request.FaceVerifyEnabled,   // SEC-1: face-verify opt-in (B2B)
                PassScorePct = request.PassScorePct,   // E5: ngưỡng pass/fail (null = HR quyết tay)
                // C11: JD/Criteria nhập text trực tiếp → *_text set, *_file_url null (không file lúc tạo).
                JDText = NormalizeText(request.JdText),
                CriteriaText = NormalizeText(request.CriteriaText),
                StartsAt = request.StartsAt,
                ExpiresAt = request.ExpiresAt,
                // Set trong code (như AddAudit/questions) → chạy được trên SQLite test + Postgres,
                // không phụ thuộc default DB `now()`.
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            // ── 3. Build questions ──────────────────────────────
            campaign.Questions = request.Questions
                .Select(q => new CampaignQuestion
                {
                    OrgId = orgId,
                    QuestionText = q.QuestionText.Trim(),
                    Source = q.Source,
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

        public async Task<List<CampaignResponse>> GetCampaignsAsync(Guid orgId, CancellationToken ct)
        {
            var listCampaigns = _db.Campaigns
                .Where(c => c.OrgId == orgId)
                .Include(c => c.Questions)
                .Include(c => c.Criteria)   // list card hiện đúng số tiêu chí (khớp detail — C12)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync(ct);

            return (await listCampaigns).Select(CampaignResponse.FromEntity).ToList();
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
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // ── 2. Only update fields that were actually provided
            if (request.Title is not null)
                campaign.Title = request.Title;

            if (request.Domain is not null)
                campaign.Domain = request.Domain;

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

            // C11: cập nhật JD/Criteria dạng text → set *_text, xoá *_file_url (text ưu tiên file).
            if (request.JdText is not null)
            {
                campaign.JDText = NormalizeText(request.JdText);
                campaign.JDFileUrl = null;
            }

            if (request.CriteriaText is not null)
            {
                campaign.CriteriaText = NormalizeText(request.CriteriaText);
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
            // ── 3. Replace existing questions with new ones ─────
            campaign.Questions.Clear();
            campaign.Questions = questions.Select(q => new CampaignQuestion
            {
                OrgId = campaign.OrgId,
                QuestionText = q.QuestionText.Trim(),
                Source = q.Source,
                IsRequired = q.IsRequired,
                CreatedAt = DateTime.UtcNow,
            }).ToList();
            campaign.UpdatedAt = DateTime.UtcNow;
            AddAudit(actorUserId, orgId, AuditAction.EditQuestions, campaign.Id, $"Thay {questions.Count} câu hỏi");
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
            if (campaign.MaxCandidates.HasValue)
            {
                var currentCount = existingEmails.Count;
                if (currentCount + toCreate.Count > campaign.MaxCandidates.Value)
                    throw new ArgumentException(
                        $"Vượt giới hạn max_candidates ({campaign.MaxCandidates.Value}): hiện có {currentCount} lời mời, đang mời thêm {toCreate.Count}.");
            }

            // ── 4. Tạo rows + đẩy job email queue ────────────────────────────
            var now = DateTime.UtcNow;
            var invitations = toCreate.Select(email => new CampaignInvitation
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                CampaignCandidateId = null,   // đường 1 (D1) — không gắn campaign_candidates
                Token = GenerateInvitationToken(),
                Email = email,
                ExpiresAt = campaign.ExpiresAt,
                CreatedAt = now,
            }).ToList();

            if (invitations.Count > 0)
            {
                _db.CampaignInvitations.AddRange(invitations);

                // DB2b — Transactional Outbox: ghi outbox-row CÙNG SaveChanges tạo invitation (thay
                // "publish best-effort SAU commit" cũ = dual-write mất mail khi broker down giữa 2 lần
                // SaveChanges). SentAt = "đã vào outbox" (dispatcher publish sau). Response giữ shape cũ.
                foreach (var invitation in invitations)
                {
                    invitation.SentAt = now;
                    _db.OutboxMessages.Add(OutboxMessage.ForInvitation(new InvitationEmailJob(
                        invitation.Id, campaign.Id, invitation.Email, invitation.Token, campaign.Title, invitation.ExpiresAt)));
                    response.Created.Add(new InvitationItem { Id = invitation.Id, Email = invitation.Email, ExpiresAt = invitation.ExpiresAt });
                }

                AddAudit(actorUserId, orgId, AuditAction.Invite, campaign.Id, $"Mời {invitations.Count} ứng viên qua email");
                await _db.SaveChangesAsync(ct);
            }

            return response;
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
            if (campaign.MaxCandidates.HasValue && toInvite.Count > 0)
            {
                var currentCount = existingEmails.Count;
                if (currentCount + toInvite.Count > campaign.MaxCandidates.Value)
                    throw new ArgumentException(
                        $"Vượt giới hạn max_candidates ({campaign.MaxCandidates.Value}): hiện có {currentCount} lời mời, đang mời thêm {toInvite.Count}.");
            }

            if (toInvite.Count == 0)
                return response;

            // Tạo invitation (gắn campaign_candidate_id) + set Analyzed → Invited + outbox-row CÙNG tx.
            var now = DateTime.UtcNow;
            foreach (var cand in toInvite)
            {
                var invitation = new CampaignInvitation
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaign.Id,
                    CampaignCandidateId = cand.Id,   // đường 2 — gắn shortlist (đường 1 để null)
                    Token = GenerateInvitationToken(),
                    Email = cand.Email!.Trim(),      // email đã chuẩn hoá lowercase từ C13/PATCH
                    ExpiresAt = campaign.ExpiresAt,
                    CreatedAt = now,
                    SentAt = now,                    // DB2b — "đã vào outbox" (dispatcher publish sau)
                };
                cand.Status = CvSubmissionStatus.Invited;
                cand.UpdatedAt = now;
                _db.CampaignInvitations.Add(invitation);

                // DB2b — outbox-row CÙNG SaveChanges tạo invitation (không dual-write mất mail khi broker down).
                _db.OutboxMessages.Add(OutboxMessage.ForInvitation(new InvitationEmailJob(
                    invitation.Id, campaign.Id, invitation.Email, invitation.Token, campaign.Title, invitation.ExpiresAt)));

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

            var fresh = new CampaignInvitation
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                CampaignCandidateId = old.CampaignCandidateId,   // giữ liên kết shortlist (đường 2); đường 1 = null
                Token = GenerateInvitationToken(),
                Email = old.Email,
                ExpiresAt = campaign.ExpiresAt,
                CreatedAt = now,
                SentAt = now,                                    // DB2b — "đã vào outbox" (dispatcher publish sau)
            };
            _db.CampaignInvitations.Add(fresh);

            // DB2b — outbox-row CÙNG transaction (revoke token cũ + tạo fresh + outbox = 1 SaveChanges).
            // Thay "resend best-effort SAU commit" cũ (mất mail khi broker down) — dispatcher publish sau.
            _db.OutboxMessages.Add(OutboxMessage.ForInvitation(new InvitationEmailJob(
                fresh.Id, campaign.Id, fresh.Email, fresh.Token, campaign.Title, fresh.ExpiresAt)));

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

            // SEC-4: gom cờ chống gian lận theo buổi → signal_type + count (+ 1 note đại diện) cho HR.
            // Đọc read-model LOCAL session_flags (không xuyên service). Campaign không bật anti-cheat → không có cờ → [].
            var flagsBySession = await GetFlagsBySessionAsync(id, ct);

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

                results.Add(new CampaignResultRow
                {
                    Rank = rank,
                    CandidateId = r.CandidateId,
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

            return new CampaignResultsResponse
            {
                CampaignId = id,
                PassScorePct = threshold,
                TotalCandidates = results.Count,
                Results = results
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

        // SEC-4: gom session_flags của 1 campaign → Dictionary<session_id, List<FlagDto>>.
        // Group theo (session_id, signal_type) → count; Note = ghi chú non-empty đầu tiên (đại diện cho HR).
        // Materialize rồi group in-memory (số cờ/campaign nhỏ; tránh phụ thuộc dịch GROUP BY của provider).
        private async Task<Dictionary<Guid, List<FlagDto>>> GetFlagsBySessionAsync(Guid campaignId, CancellationToken ct)
        {
            var flags = await _db.SessionFlags
                .Where(f => f.CampaignId == campaignId)
                .ToListAsync(ct);

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

        // ── E6: xuất bảng kết quả (E5) ra file ──────────────────────────────
        // Tái dùng NGUYÊN VẸN GetCampaignResultsAsync (E5) → thứ tự + rank + pass/fail y hệt bảng web,
        // không tính lại (một nguồn sự thật). Ngoài org → E5 ném KeyNotFoundException → controller 404.
        // format: null/"" → mặc định csv; "csv" → csv; khác (kể cả "pdf" 🔜) → ArgumentException → 400.
        public async Task<CampaignResultExport> ExportCampaignResultsAsync(
            Guid orgId, Guid id, string? format, CancellationToken ct)
        {
            var normalized = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
            if (normalized == "pdf")
                throw new ArgumentException("format 'pdf' chưa được hỗ trợ — dùng format=csv.");
            if (normalized != "csv")
                throw new ArgumentException($"format '{format}' không hợp lệ — chỉ hỗ trợ format=csv.");

            var results = await GetCampaignResultsAsync(orgId, id, ct);   // có thể ném KeyNotFoundException (404)

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
            if (campaign.MaxCandidates.HasValue)
            {
                var currentCount = await _db.CvSubmissions.CountAsync(c => c.CampaignId == id, ct);
                if (currentCount + files.Count > campaign.MaxCandidates.Value)
                    throw new ArgumentException(
                        $"Vượt giới hạn sàng lọc của gói (max_candidates={campaign.MaxCandidates.Value}): " +
                        $"hiện có {currentCount} CV, đang tải thêm {files.Count}.");
            }

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
        // Cột snake_case (§5): rank,candidate_id,session_id,total_score,result,scored_at.
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
                Flags = string.Join("; ", r.Flags.Select(f => $"{f.Type}:{f.Count}"))
            }).ToList();

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
            public int Rank { get; set; }
            public Guid CandidateId { get; set; }
            public Guid SessionId { get; set; }
            public decimal TotalScore { get; set; }
            public string Result { get; set; } = string.Empty;
            public DateTime ScoredAt { get; set; }
            public string Flags { get; set; } = string.Empty;   // SEC-4: tóm tắt cờ chống gian lận
        }

        private sealed class ResultCsvRowMap : ClassMap<ResultCsvRow>
        {
            public ResultCsvRowMap()
            {
                Map(m => m.Rank).Index(0).Name("rank");
                Map(m => m.CandidateId).Index(1).Name("candidate_id");
                Map(m => m.SessionId).Index(2).Name("session_id");
                Map(m => m.TotalScore).Index(3).Name("total_score");
                Map(m => m.Result).Index(4).Name("result");
                // ScoredAt là UTC (UpdatedAt server) → ISO 8601 với hậu tố Z (chữ hoa = literal).
                Map(m => m.ScoredAt).Index(5).Name("scored_at").TypeConverterOption.Format("yyyy-MM-ddTHH:mm:ssZ");
                Map(m => m.Flags).Index(6).Name("flags");   // SEC-4: cột cờ chống gian lận (rỗng nếu không có)
            }
        }

        // E5: ngưỡng pass/fail là % điểm tổng → phải ∈ [0,100] khi có (null = HR quyết tay).
        private static void ValidatePassScorePct(int? pct)
        {
            if (pct is int p && (p < 0 || p > 100))
                throw new ArgumentException($"pass_score_pct phải trong khoảng [0, 100] (hiện: {p}).");
        }

        // Token magic-link 1 lần — 256-bit random, URL-safe base64 (không padding).
        private static string GenerateInvitationToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
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
            var suggested = await _suggester.SuggestAsync(jobCategory, campaign.JDText, campaign.CriteriaText, 4, ct);

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

        // C11: chuẩn hoá JD/Criteria nhập trực tiếp — trim; rỗng/whitespace → null.
        private static string? NormalizeText(string? text)
            => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

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
