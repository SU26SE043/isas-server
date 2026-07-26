using Isas.InterviewService.DTOs;
namespace Isas.InterviewService.Services.Interfaces;
public interface IAiServiceRepoAnalyzer { Task<RepoAnalysisAiResult> AnalyzeAsync(string repoDigest, string jobCategory, string? jdText, CancellationToken ct = default); }
