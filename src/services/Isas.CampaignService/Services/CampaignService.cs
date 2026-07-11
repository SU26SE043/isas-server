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
                StartsAt = request.StartsAt,
                ExpiresAt = request.ExpiresAt,
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

            if (request.JdFile is not null) ValidateFile(request.JdFile, "JD");
            if (request.CriteriaFile is not null) ValidateFile(request.CriteriaFile, "Criteria");

            var jdTask = HandleFileAsync(request.JdFile, campaign.Id, "jd", ct);
            var criteriaTask = HandleFileAsync(request.CriteriaFile, campaign.Id, "criteria", ct);

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

            if (request.StartsAt.HasValue)
                campaign.StartsAt = request.StartsAt;

            if (request.ExpiresAt.HasValue)
                campaign.ExpiresAt = request.ExpiresAt;

            // ── 3. Persist ───────────────────────────────────────
            campaign.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

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

            if (request.JdFile is not null) ValidateFile(request.JdFile, "JD");
            if (request.CriteriaFile is not null) ValidateFile(request.CriteriaFile, "Criteria");

            // ── Delete old files from SeaweedFS before uploading ─
            if (request.JdFile is not null && !string.IsNullOrWhiteSpace(campaign.JDFileUrl))
                await _file.DeleteAsync(campaign.JDFileUrl, ct);

            if (request.CriteriaFile is not null && !string.IsNullOrWhiteSpace(campaign.CriteriaFileUrl))
                await _file.DeleteAsync(campaign.CriteriaFileUrl, ct);

            // ── Upload new files ──────────────────────────────────
            var jdTask = HandleFileAsync(request.JdFile, campaign.Id, "jd", ct);
            var criteriaTask = HandleFileAsync(request.CriteriaFile, campaign.Id, "criteria", ct);

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

        // Token magic-link 1 lần — 256-bit random, URL-safe base64 (không padding).
        private static string GenerateInvitationToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
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
            var criteria = suggested.Select(s => new CampaignCriterion
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                Name = s.Name,
                Description = s.Description,
                Weight = Math.Round(s.Weight / total, 4),
                MaxScore = s.MaxScore <= 0 ? 5 : s.MaxScore,
                Source = CriterionSource.AiSuggested,
                CreatedAt = DateTime.UtcNow
            }).ToList();
            criteria[0].Weight += 1m - criteria.Sum(c => c.Weight);   // sửa sai số làm tròn → Σ=1
            return criteria;
        }

        // Fallback khi AIService không khả dụng — Σweight = 1 (0.4+0.3+0.3). Id/CreatedAt set sẵn.
        private static List<CampaignCriterion> BuildDefaultCriteria(Guid campaignId) => new()
        {
            new() { Id = Guid.NewGuid(), CampaignId = campaignId, Name = "Kiến thức chuyên môn", Weight = 0.4m, MaxScore = 5, Source = CriterionSource.AiSuggested, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), CampaignId = campaignId, Name = "Giao tiếp / trình bày", Weight = 0.3m, MaxScore = 5, Source = CriterionSource.AiSuggested, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), CampaignId = campaignId, Name = "Giải quyết vấn đề",     Weight = 0.3m, MaxScore = 5, Source = CriterionSource.AiSuggested, CreatedAt = DateTime.UtcNow },
        };

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
