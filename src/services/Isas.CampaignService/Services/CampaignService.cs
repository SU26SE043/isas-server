using Amazon.S3.Model;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    public class CampaignService : ICampaignService
    {
        private readonly ILogger<CampaignService> _logger;
        private readonly CampaignDbContext _db; 
        private readonly IFileService _file;
        private readonly IParserService _parser;
        private static readonly HashSet<string> AllowedMimeTypes = new()
            {
                "application/pdf",
            };
        private const long MaxFileSizeBytes = 10 * 1024 * 1024;

        public CampaignService(CampaignDbContext db, 
            IFileService file, ILogger<CampaignService> logger, 
            IParserService parser)
        {
            _db = db;
            _file = file;
            _logger = logger;
            _parser = parser;
        }

        public async Task<CampaignResponse> CreateCampaignAsync(Guid employerId, CreateCampaignRequest request, CancellationToken ct = default)
        {
            // ── 1. Validate questions ───────────────────────────
            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                throw new ArgumentException("All questions must have non-empty text.");

            // ── 2. Build campaign entity ────────────────────────
            var campaign = new Campaign
            {
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

            // ── 4. Persist campaign first (to get Id) ───────────
            _db.Campaigns.Add(campaign);
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

        public async Task<CampaignResponse> GetCampaignAsync(Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<List<CampaignResponse>> GetCampaignsAsync(CancellationToken ct)
        {
            var listCampaigns = _db.Campaigns
                .Include(c => c.Questions)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync(ct);

            return (await listCampaigns).Select(CampaignResponse.FromEntity).ToList();
        }

        public async Task<CampaignResponse> UpdateCampaignAsync(Guid id, UpdateCampaignRequest request, CancellationToken ct)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id, ct)
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

            campaign.AntiCheatEnabled = request.AntiCheatEnabled;

            if (request.StartsAt.HasValue)
                campaign.StartsAt = request.StartsAt;

            if (request.ExpiresAt.HasValue)
                campaign.ExpiresAt = request.ExpiresAt;

            // ── 3. Persist ───────────────────────────────────────
            campaign.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<CampaignResponse> UpdateCampaignFilesAsync(Guid id, UploadCampaignFilesRequest request, CancellationToken ct = default)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

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

        public async Task<CampaignResponse> UpdateCampaignQuestionsAsync(Guid id, List<QuestionItem> questions, CancellationToken ct)
        {
            // ── 1. Fetch & verify ownership ─────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");
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
            await _db.SaveChangesAsync(ct);
            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<bool> DeleteCampaignAsync(Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new KeyNotFoundException($"Campaign {id} not found.");

            var questions = await _db.CampaignQuestions.Where(q => q.CampaignId == id).ToListAsync(ct);

            // Delete associated files from SeaweedFS
            if (!string.IsNullOrWhiteSpace(campaign.JDFileUrl))
                await _file.DeleteAsync(campaign.JDFileUrl, ct);

            if (!string.IsNullOrWhiteSpace(campaign.CriteriaFileUrl))
                await _file.DeleteAsync(campaign.CriteriaFileUrl, ct);

            _db.CampaignQuestions.RemoveRange(questions);
            _db.Campaigns.Remove(campaign);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        private static void ValidateFile(IFormFile file, string label)
        {
            if (file.Length == 0)
                throw new ArgumentException($"{label} file is empty.");

            if (file.Length > MaxFileSizeBytes)
                throw new ArgumentException($"{label} file exceeds the 10 MB limit.");

            if (!AllowedMimeTypes.Contains(file.ContentType))
                throw new ArgumentException(
                    $"{label} file type '{file.ContentType}' is not allowed. " +
                    "Only PDF and DOCX are accepted.");
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

            return (label, _file.GetUrl(path), parsedText);
        }
    }
}
