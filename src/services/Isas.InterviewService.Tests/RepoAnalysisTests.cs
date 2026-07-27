using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Isas.InterviewService.Tests;

public class RepoAnalysisTests
{
    private static RepoAnalysisService Service(TestDb t, Mock<IGitHubRepoFetcher> fetcher, Mock<IAiServiceRepoAnalyzer> ai, Mock<ICreditReservationClient> credits, int cost = 1)
        => new(t.Db, ai.Object, fetcher.Object, credits.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Billing:RepoAnalysisCredits"] = cost.ToString() }).Build());

    [Fact]
    public async Task Analyze_ValidPublicRepo_ReservesFetchesConsumesAndPersists()
    {
        using var t = new TestDb(); var user = Guid.NewGuid();
        var fetcher = new Mock<IGitHubRepoFetcher>();
        fetcher.Setup(x => x.FetchAsync("dotnet", "aspnetcore", It.IsAny<CancellationToken>())).ReturnsAsync(new GitHubRepoDigest("main", "abc", 10, "C#", new() { ["C#"] = 100 }, "digest"));
        var ai = new Mock<IAiServiceRepoAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync("digest", "BE", null, It.IsAny<CancellationToken>())).ReturnsAsync(new RepoAnalysisAiResult("summary", [".NET"], ["clean"], [], [], ["DI"], null));
        var credits = new Mock<ICreditReservationClient>(); credits.Setup(x => x.ReserveAsync("User", user, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        var result = await Service(t, fetcher, ai, credits).AnalyzeAsync(user, new RepoAnalysisRequest("https://github.com/dotnet/aspnetcore", JobCategory.BE));
        Assert.Equal("abc", result.CommitSha); Assert.Single(await t.Db.RepoAnalyses.ToListAsync());
        credits.Verify(x => x.ConsumeAsync(result.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Analyze_InvalidOrTyposquatUrl_DoesNotReserveOrFetch()
    {
        using var t = new TestDb(); var fetcher = new Mock<IGitHubRepoFetcher>(); var ai = new Mock<IAiServiceRepoAnalyzer>(); var credits = new Mock<ICreditReservationClient>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(t, fetcher, ai, credits).AnalyzeAsync(Guid.NewGuid(), new RepoAnalysisRequest("https://github.com.evil.com/a/b", JobCategory.BE)));
        credits.Verify(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fetcher.Verify(x => x.FetchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Analyze_FetchFails_ReleasesReservation()
    {
        using var t = new TestDb(); var user=Guid.NewGuid(); var fetcher = new Mock<IGitHubRepoFetcher>(); fetcher.Setup(x => x.FetchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException()); var ai = new Mock<IAiServiceRepoAnalyzer>(); var credits = new Mock<ICreditReservationClient>(); credits.Setup(x => x.ReserveAsync("User", user, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CreditReservationResult(Guid.NewGuid(),1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(t,fetcher,ai,credits).AnalyzeAsync(user,new RepoAnalysisRequest("https://github.com/a/b",JobCategory.BE)));
        credits.Verify(x => x.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
