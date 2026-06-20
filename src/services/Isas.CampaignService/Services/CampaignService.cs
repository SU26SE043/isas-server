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
            // ── 1. Validate files ───────────────────────────────
            if (request.JdFile is not null) 
                ValidateFile(request.JdFile, "JD");

            if (request.CriteriaFile is not null) 
                ValidateFile(request.CriteriaFile, "Criteria");

            // ── 2. Validate questions ───────────────────────────
            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                throw new ArgumentException("All questions must have non-empty text.");

            // ── 3. Build campaign entity ────────────────────────
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

            // ── 4. Build questions ──────────────────────────────
            campaign.Questions = request.Questions
                .Select(q => new CampaignQuestion
                {
                    EmployerId = employerId,
                    QuestionText = q.QuestionText.Trim(),
                    Source = q.Source,
                    TimeLimitSeconds = q.TimeLimitSeconds,
                    IsRequired = q.IsRequired,
                    CreatedAt = DateTime.UtcNow,
                })
                .ToList();

            // ── 5. Handle file parsing + upload in parallel ─────
            var jdTask = HandleFileAsync(request.JdFile, campaign.Id, "jd", ct);
            var criteriaTask = HandleFileAsync(request.CriteriaFile, campaign.Id, "criteria", ct);

            // ── 6. Persist campaign first (to get Id) ───────────
            _db.Campaigns.Add(campaign);
            await _db.SaveChangesAsync(ct);

            // ── 7. Await file tasks (parallel) ──────────────────
            var results = await Task.WhenAll(jdTask, criteriaTask);

            // Attach results if any
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

            // ── 8. Save once with file URLs ─────────────────────
            if (results.Any(r => r is not null))
            {
                _db.Campaigns.Update(campaign);
                await _db.SaveChangesAsync(ct);
            }

            return CampaignResponse.FromEntity(campaign);
        }

        public async Task<bool> DeleteCampaignAsync(Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);
            if(campaign == null)
            {
                return false;
            }

            _db.Campaigns.Remove(campaign);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<CampaignResponse> GetCampaignAsync(Guid id, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .FirstOrDefaultAsync(c => c.Id == id, ct);

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
            var campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);

            campaign.Title = request.Title;
            campaign.Domain = request.Domain;
            campaign.MaxCandidates = request.MaxCandidates;
            campaign.TimeLimitMinutes = request.TimeLimitMinutes;
            campaign.AntiCheatEnabled = request.AntiCheatEnabled;
            campaign.StartsAt = request.StartsAt;
            campaign.ExpiresAt = request.ExpiresAt;
            campaign.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            return CampaignResponse.FromEntity(campaign);
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
