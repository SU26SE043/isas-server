using Amazon.S3.Model;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

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
        private static readonly HashSet<string> AllowedMimeTypes = new()
            {
                "application/pdf",
            };
        private const long MaxFileSizeBytes = 10 * 1024 * 1024;

        public CampaignService(CampaignDbContext db,
            IFileService file, ILogger<CampaignService> logger,
            IParserService parser, ICriteriaSuggester suggester,
            IInvitationEmailPublisher emailPublisher)
        {
            _db = db;
            _file = file;
            _logger = logger;
            _parser = parser;
            _suggester = suggester;
            _emailPublisher = emailPublisher;
        }

        public async Task<CampaignResponse> CreateCampaignAsync(Guid employerId, CreateCampaignRequest request, CancellationToken ct = default)
        {
            // ── 1. Validate questions ───────────────────────────
            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                throw new ArgumentException("All questions must have non-empty text.");

            ValidatePassScorePct(request.PassScorePct);   // E5: ngưỡng ∈ [0,100] nếu có

            // ── 2. Build campaign entity ────────────────────────
            var campaign = new Campaign
            {
                Id = Guid.NewGuid(),
                EmployerId = employerId,
                Title = request.Title,
                Domain = request.Domain,
                Status = CampaignStatus.Draft,
                MaxCandidates = request.MaxCandidates,
                TimeLimitMinutes = request.TimeLimitMinutes,
                AntiCheatEnabled = request.AntiCheatEnabled,
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
                    EmployerId = employerId,
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
                AddAudit(employerId, AuditAction.EditCriteria, campaign.Id, $"Khai {campaign.Criteria.Count} tiêu chí (HrEdited)");
            }

            // ── 4. Persist campaign + audit (C10) ───────────────
            _db.Campaigns.Add(campaign);
            AddAudit(employerId, AuditAction.CreateCampaign, campaign.Id, $"Tạo campaign '{campaign.Title}'");
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<CampaignResponse> UploadCampaignFilesAsync(Guid employerId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct = default)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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
         
        public async Task<Stream> DownloadCampaignFilesAsync(Guid employerId, Guid id, string fileType, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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

        public async Task<CampaignResponse> GetCampaignAsync(Guid employerId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)   // C12: trả tiêu chí structured để HR xem/duyệt
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<List<CampaignResponse>> GetCampaignsAsync(Guid employerId, CancellationToken ct)
        {
            var listCampaigns = _db.Campaigns
                .Where(c => c.EmployerId == employerId)
                .Include(c => c.Questions)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync(ct);

            return (await listCampaigns).Select(CampaignResponse.FromEntity).ToList();
        }

        public async Task<CampaignResponse> UpdateCampaignAsync(Guid employerId, Guid id, UpdateCampaignRequest request, CancellationToken ct)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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
                AddAudit(employerId, AuditAction.EditCriteria, campaign.Id, $"Ghi đè {rebuiltCriteria.Count} tiêu chí (HrEdited)");
                await _db.SaveChangesAsync(ct);
                campaign.Criteria = rebuiltCriteria;                 // đồng bộ nav cho response (bộ cũ đã xoá)
            }

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<CampaignResponse> UpdateCampaignFilesAsync(Guid employerId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct = default)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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

        public async Task<CampaignResponse> UpdateCampaignQuestionsAsync(Guid employerId, Guid id, List<QuestionItem> questions, CancellationToken ct)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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
                EmployerId = campaign.EmployerId,
                QuestionText = q.QuestionText.Trim(),
                Source = q.Source,
                IsRequired = q.IsRequired,
                CreatedAt = DateTime.UtcNow,
            }).ToList();
            campaign.UpdatedAt = DateTime.UtcNow;
            AddAudit(employerId, AuditAction.EditQuestions, campaign.Id, $"Thay {questions.Count} câu hỏi");
            await _db.SaveChangesAsync(ct);
            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<bool> DeleteCampaignAsync(Guid employerId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // Soft delete (D11): giữ campaign + câu hỏi + file cho audit/đối chất.
            // KHÔNG xoá file ngay — cronjob purge SeaweedFS sau 90 ngày.
            campaign.DeletedAt = DateTime.UtcNow;
            campaign.UpdatedAt = DateTime.UtcNow;
            AddAudit(employerId, AuditAction.Delete, campaign.Id, "Soft delete");
            await _db.SaveChangesAsync(ct);
            return true;
        }

        // ── PUBLISH (C8): Draft → Active + sinh tiêu chí CÓ CẤU TRÚC ────────
        public async Task<CampaignResponse> PublishCampaignAsync(Guid employerId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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
            AddAudit(employerId, AuditAction.Publish, campaign.Id, "Publish: Draft → Active");
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        // ── TRANSITION (C7): chỉ tiến Active→Closed→Archived (Draft→Active dùng publish) ──
        public async Task<CampaignResponse> TransitionStatusAsync(Guid employerId, Guid id, CampaignStatus target, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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
            AddAudit(employerId, AuditAction.TransitionStatus, campaign.Id, $"{from} → {target}");
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        // ── D1: Distribution đường 1 — mời thẳng qua danh sách email ────────
        // Thứ tự xử lý (đúng doc): validate định dạng → dedup → cap max_candidates.
        // Email hỏng/trùng/đã mời → failed[] per-item, KHÔNG chặn cả batch.
        // Vượt cap max_candidates → chặn CẢ request (ArgumentException → 400).
        public async Task<CreateInvitationsResponse> CreateInvitationsAsync(Guid employerId, Guid id, List<string> emails, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
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
                AddAudit(employerId, AuditAction.Invite, campaign.Id, $"Mời {invitations.Count} ứng viên qua email");
                await _db.SaveChangesAsync(ct);

                foreach (var invitation in invitations)
                {
                    await _emailPublisher.PublishAsync(new InvitationEmailJob(
                        invitation.Id, campaign.Id, invitation.Email, invitation.Token, campaign.Title, invitation.ExpiresAt), ct);

                    invitation.SentAt = DateTime.UtcNow;
                    response.Created.Add(new InvitationItem { Id = invitation.Id, Email = invitation.Email, ExpiresAt = invitation.ExpiresAt });
                }

                await _db.SaveChangesAsync(ct);   // persist SentAt sau khi đẩy queue
            }

            return response;
        }

        // ── E5: bảng kết quả + xếp hạng + pass/fail ─────────────────────────
        // Đọc read-model LOCAL `campaign_rankings` (E4 upsert từ event SessionScored) — không gọi
        // xuyên service. Bảng chỉ có 1 row/ứng viên đã `Scored` (row tạo khi nhận SessionScored) nên
        // "chỉ xếp hạng Scored" (CAMP-11) tự thoả — ứng viên chưa Scored không có row → không xuất hiện.
        // Ownership: chỉ chủ org (employer_id) xem được — không phải chủ → 404 (KeyNotFoundException).
        public async Task<CampaignResultsResponse> GetCampaignResultsAsync(Guid employerId, Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id && c.EmployerId == employerId, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            // Materialize rồi sắp + gán rank TRONG BỘ NHỚ: EF Core không dịch ROW_NUMBER()/RANK() sang
            // LINQ; và trên SQLite (test) decimal lưu dạng TEXT → ORDER BY ở SQL có thể sai thứ tự số học.
            // Số row/1 campaign bị chặn bởi max_candidates (nhỏ) nên sort in-memory an toàn.
            var rows = await _db.CampaignRankings
                .Where(r => r.CampaignId == id)
                .ToListAsync(ct);

            var ordered = rows
                .OrderByDescending(r => r.TotalScore)
                .ThenBy(r => r.UpdatedAt)   // đồng điểm → ứng viên Scored sớm hơn đứng trước (tie-break ổn định)
                .ThenBy(r => r.SessionId)
                .ToList();

            var threshold = campaign.PassScorePct;
            var results = new List<CampaignResultRow>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];

                // Đồng hạng (competition ranking): rank = số ứng viên điểm CAO HƠN + 1.
                // Đồng điểm → cùng rank; rank kế nhảy theo vị trí (1,1,3).
                int rank = (i > 0 && ordered[i - 1].TotalScore == r.TotalScore)
                    ? results[i - 1].Rank
                    : i + 1;

                results.Add(new CampaignResultRow
                {
                    Rank = rank,
                    CandidateId = r.CandidateId,
                    SessionId = r.SessionId,
                    TotalScore = r.TotalScore,
                    // Pass/fail so ngưỡng Employer (CAMP-11); ngưỡng null → null (HR quyết tay — doc §pass_score_pct).
                    Result = threshold is null
                        ? null
                        : (r.TotalScore >= threshold.Value ? "Pass" : "Fail"),
                    ScoredAt = r.UpdatedAt
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

        // C10: ghi vết thao tác. Id/At set sẵn (chạy được trên SQLite test + Postgres).
        private void AddAudit(Guid actorId, AuditAction action, Guid entityId, string? summary)
            => _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = actorId,
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
