using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class JdRequirementServiceTests
{
    [Fact]
    public async Task TextWinsOverFile_AndRetrievesGroundingBeforeAi()
    {
        var candidate = Guid.NewGuid();
        var jdId = Guid.NewGuid();
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        var knowledge = new Mock<IKnowledgeService>();
        var ai = new Mock<IAiServiceCvAnalyzer>();
        var grounding = new[] { new GroundingChunk("chunk-1", "Docker docs", "", "Docker") };

        knowledge.Setup(x => x.RetrieveAsync("BE", "Need Docker", It.IsAny<CancellationToken>()))
            .ReturnsAsync(grounding);
        ai.Setup(x => x.SuggestJdRequirementsAsync(
                "BE", "Need Docker", grounding, It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<JdRequirementSuggestion>)[
                    new("Docker", [new Citation("chunk-1", "", "Docker")])
                ],
                (IReadOnlyList<JdRequirementSuggestion>)[]));

        var sut = new JdRequirementService(
            storage.Object, knowledge.Object, ai.Object,
            NullLogger<JdRequirementService>.Instance);

        var result = await sut.SuggestAsync(candidate,
            new JdRequirementsRequest("Need Docker", jdId, JobCategory.BE));

        Assert.Single(result.MustHave);
        storage.Verify(x => x.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        knowledge.VerifyAll();
        ai.VerifyAll();
    }

    [Fact]
    public async Task FileFromAnotherCandidate_IsForbiddenBeforeRetrieval()
    {
        var candidate = Guid.NewGuid();
        var jdId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(x => x.GetMetadata(jdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Isas.InterviewService.Entities.FileRecord { Id = jdId, UserId = Guid.NewGuid() });

        var knowledge = new Mock<IKnowledgeService>(MockBehavior.Strict);
        var ai = new Mock<IAiServiceCvAnalyzer>(MockBehavior.Strict);
        var sut = new JdRequirementService(
            storage.Object, knowledge.Object, ai.Object,
            NullLogger<JdRequirementService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.SuggestAsync(
            candidate, new JdRequirementsRequest(null, jdId, JobCategory.FE)));
        knowledge.VerifyNoOtherCalls();
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileTextOverLimit_IsTruncatedBeforeRetrieval()
    {
        var candidate = Guid.NewGuid();
        var jdId = Guid.NewGuid();
        var fileText = new string('x', Isas.Shared.Validation.TextInputLimits.JdTextMaxChars + 12);
        var storage = new Mock<IStorageService>();
        storage.Setup(x => x.GetMetadata(jdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Isas.InterviewService.Entities.FileRecord
            {
                Id = jdId, UserId = candidate, ParsedText = fileText
            });
        var knowledge = new Mock<IKnowledgeService>();
        knowledge.Setup(x => x.RetrieveAsync("BA", It.Is<string>(s => s.Length == Isas.Shared.Validation.TextInputLimits.JdTextMaxChars), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GroundingChunk>());
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.SuggestJdRequirementsAsync(
                "BA",
                It.Is<string>(s => s.Length == Isas.Shared.Validation.TextInputLimits.JdTextMaxChars),
                It.Is<IReadOnlyList<GroundingChunk>>(x => x.Count == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<JdRequirementSuggestion>)[], (IReadOnlyList<JdRequirementSuggestion>)[]));

        var sut = new JdRequirementService(
            storage.Object, knowledge.Object, ai.Object,
            NullLogger<JdRequirementService>.Instance);

        var result = await sut.SuggestAsync(candidate,
            new JdRequirementsRequest(null, jdId, JobCategory.BA));

        Assert.Empty(result.MustHave);
        knowledge.VerifyAll();
        ai.VerifyAll();
    }
}
