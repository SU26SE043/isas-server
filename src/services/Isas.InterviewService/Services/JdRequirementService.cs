using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Validation;

namespace Isas.InterviewService.Services;

public class JdRequirementService(
    IStorageService storage,
    IKnowledgeService knowledge,
    IAiServiceCvAnalyzer analyzer,
    ILogger<JdRequirementService> logger) : IJdRequirementService
{
    private const string JdTextLabel = "Mô tả công việc (jdText)";

    public async Task<JdRequirementsResponse> SuggestAsync(
        Guid candidateId, JdRequirementsRequest request, CancellationToken ct = default)
    {
        if (request.JobCategory is null)
            throw new InvalidOperationException("jobCategory là bắt buộc.");

        var jdText = TextInputLimits.NormalizeAndEnsureLimit(
            request.JdText, JdTextLabel, message => new InvalidOperationException(message));

        Guid? jdIdToUse = jdText is not null ? null : request.JdId;
        if (jdText is null && jdIdToUse is null)
            throw new InvalidOperationException("Cần cung cấp jdText hoặc jdId.");

        if (jdText is null && jdIdToUse is not null)
        {
            var file = await storage.GetMetadata(jdIdToUse.Value, ct)
                ?? throw new KeyNotFoundException("JD không tồn tại");
            if (file.UserId != candidateId)
                throw new UnauthorizedAccessException("Không phải JD của bạn");
            if (string.IsNullOrWhiteSpace(file.ParsedText))
                throw new InvalidOperationException("JD không đọc được nội dung");

            jdText = file.ParsedText;
            if (jdText.Length > TextInputLimits.JdTextMaxChars)
            {
                logger.LogWarning(
                    "JD file {JdId} bị cắt từ {OriginalChars} xuống {MaxChars} ký tự trước khi tách requirement",
                    jdIdToUse, jdText.Length, TextInputLimits.JdTextMaxChars);
                jdText = jdText[..TextInputLimits.JdTextMaxChars];
                // Cắt file đã upload theo chủ đích: user không thể sửa nội dung file tại endpoint này.
            }
        }

        var grounding = await knowledge.RetrieveAsync(
            request.JobCategory.Value.ToString(), jdText!, ct);
        var result = await analyzer.SuggestJdRequirementsAsync(
            request.JobCategory.Value.ToString(), jdText!, grounding, ct);

        return new JdRequirementsResponse(result.MustHave, result.NiceToHave);
    }
}
