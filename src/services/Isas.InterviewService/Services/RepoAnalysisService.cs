using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Isas.Shared.Validation;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

public class RepoAnalysisService(InterviewDbContext db, IAiServiceRepoAnalyzer analyzer, IGitHubRepoFetcher fetcher, ICreditReservationClient credits, IConfiguration config) : IRepoAnalysisService
{
    private readonly int _cost = int.TryParse(config["Billing:RepoAnalysisCredits"], out var cost) ? cost : 1;
    public async Task<RepoAnalysisResponse> AnalyzeAsync(Guid candidateId, RepoAnalysisRequest req, CancellationToken ct = default)
    {
        if (req.JobCategory is null) throw new InvalidOperationException("jobCategory là bắt buộc.");
        if (!Uri.TryCreate(req.RepoUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || (uri.Host != "github.com" && uri.Host != "www.github.com") || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 && !uri.IsDefaultPort)
            throw new InvalidOperationException("repoUrl phải là URL HTTPS GitHub hợp lệ.");
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$") || !System.Text.RegularExpressions.Regex.IsMatch(parts[1].Replace(".git", ""), "^[A-Za-z0-9_.-]{1,100}$")) throw new InvalidOperationException("repoUrl không hợp lệ.");
        var jd = TextInputLimits.NormalizeAndEnsureLimit(req.JdText, "Mô tả công việc (jdText)", m => new InvalidOperationException(m));
        var id = Guid.NewGuid();
        if (_cost > 0) await credits.ReserveAsync("User", candidateId, id, ct);
        try
        {
            var repo = await fetcher.FetchAsync(parts[0], parts[1].Replace(".git", ""), ct);
            var ai = await analyzer.AnalyzeAsync(repo.Digest, req.JobCategory.Value.ToString(), jd, ct);
            var row = new RepoAnalysis { Id=id, CandidateId=candidateId, RepoUrl=uri.GetLeftPart(UriPartial.Path), RepoOwner=parts[0], RepoName=parts[1].Replace(".git", ""), JobCategory=req.JobCategory.Value, DefaultBranch=repo.DefaultBranch, CommitSha=repo.CommitSha, Stars=repo.Stars, PrimaryLanguage=repo.PrimaryLanguage, Languages=repo.Languages, Summary=ai.Summary, TechStack=ai.TechStack, Strengths=ai.Strengths, Weaknesses=ai.Weaknesses, Suggestions=ai.Suggestions, InterviewTalkingPoints=ai.InterviewTalkingPoints, JdMatch=jd is null ? null : ai.JdMatch };
            db.RepoAnalyses.Add(row); await db.SaveChangesAsync(ct); if (_cost > 0) await credits.ConsumeAsync(id, ct); return Map(row);
        }
        catch { if (_cost > 0) { try { await credits.ReleaseAsync(id, CancellationToken.None); } catch (PaymentServiceException) { } } throw; }
    }
    public async Task<RepoAnalysisResponse?> GetAsync(Guid candidateId, Guid id, CancellationToken ct = default) { var row=await db.RepoAnalyses.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct); if(row is null)return null; if(row.CandidateId!=candidateId)throw new UnauthorizedAccessException("Không phải phân tích của bạn"); return Map(row); }
    public async Task<KeysetPage<RepoAnalysisResponse>> ListAsync(Guid candidateId,string? cursor=null,int? limit=null,CancellationToken ct=default) { var take=KeysetPaging.ClampLimit(limit); var rows=await db.RepoAnalyses.AsNoTracking().Where(x=>x.CandidateId==candidateId).OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Take(take).ToListAsync(ct); var items=rows.Select(Map).ToList(); return new(items,items.Count==take?new KeysetCursor(items[^1].CreatedAt,items[^1].Id).Encode():null); }
    private static RepoAnalysisResponse Map(RepoAnalysis x) => new(x.Id,x.RepoUrl,x.RepoOwner,x.RepoName,x.JobCategory.ToString(),x.PrimaryLanguage,x.Stars,x.Languages,x.Summary,x.TechStack,x.Strengths,x.Weaknesses,x.Suggestions,x.InterviewTalkingPoints,x.JdMatch is null?null:new JdMatchResponse(x.JdMatch.Score,x.JdMatch.MatchedSkills,x.JdMatch.MissingSkills),x.CommitSha,x.CreatedAt);
}
