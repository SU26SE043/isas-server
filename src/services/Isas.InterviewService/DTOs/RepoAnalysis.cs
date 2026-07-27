using System.ComponentModel.DataAnnotations;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.DTOs;

public record RepoAnalysisRequest(string RepoUrl, [Required] JobCategory? JobCategory, string? JdText = null, Guid? JdId = null);
public record RepoAnalysisAiResult(string Summary, List<string> TechStack, List<string> Strengths, List<string> Weaknesses, List<string> Suggestions, List<string> InterviewTalkingPoints, CvJdMatch? JdMatch);
public record RepoAnalysisResponse(Guid Id, string RepoUrl, string RepoOwner, string RepoName, string JobCategory, string? PrimaryLanguage, int Stars, IReadOnlyDictionary<string,long> Languages, string Summary, IReadOnlyList<string> TechStack, IReadOnlyList<string> Strengths, IReadOnlyList<string> Weaknesses, IReadOnlyList<string> Suggestions, IReadOnlyList<string> InterviewTalkingPoints, JdMatchResponse? JdMatch, string? CommitSha, DateTime CreatedAt);
