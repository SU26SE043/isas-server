using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Moq;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Rubric RIÊNG của người luyện (BC16) nay khai được mốc điểm, và mốc của admin đi theo template khi
/// họ clone bộ chuẩn.
///
/// <para>🔴 Không có vế này thì chính đợt thêm mốc cho bộ chuẩn lại tạo ra một nghịch lý: dùng bộ mặc
/// định thì được thang có mô tả từng mức, còn TỰ TUỲ CHỈNH thì rơi về dải mặc định "Mức 3/5" — tức tự
/// tuỳ chỉnh xong thì chất lượng chấm TỆ ĐI, và người dùng không có cách nào biết.</para>
/// </summary>
public class CustomRubricLevelsTests
{
    private static readonly List<RubricLevelInput> Levels =
    [
        new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
        new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói tới đánh đổi."),
        new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi của phương án.")
    ];

    private static UpsertRubricRequest OneCriterion(List<RubricLevelInput>? levels)
        => new([new RubricCriterionInput("Tự đặt", "mô tả", 1.0m, 5, levels)]);

    // ── (1) Khai mốc cho rubric riêng ────────────────────────────────────────────────────────

    [Fact]
    public async Task Replace_WithLevels_PersistsThem()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = new RubricLibraryService(t.Db);

        var saved = await svc.ReplaceAsync(candidate, JobCategory.BE, OneCriterion(Levels));

        Assert.True(saved.IsCustom);
        Assert.Equal(3, saved.Criteria[0].Levels.Count);
        Assert.Equal([0, 3, 5], saved.Criteria[0].Levels.Select(l => l.Score).ToArray());

        var rows = await t.Db.RubricLevels.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// Mốc khai xong phải tới được ĐƯỜNG CHẤM — nếu không thì nó chỉ là dữ liệu trang trí.
    /// <c>ScoringCriteriaBuilder</c> phải in ra mốc thật thay vì dải mặc định "Mức i/5".
    /// </summary>
    [Fact]
    public async Task CustomLevels_ReachScoringPayload_NotDefaultBand()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await new RubricLibraryService(t.Db).ReplaceAsync(candidate, JobCategory.BE, OneCriterion(Levels));

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, "vi"));
        var payload = ScoringCriteriaBuilder.Build(loaded);

        Assert.Single(payload);
        Assert.Equal([0, 3, 5], payload[0].Levels.Select(l => l.Score).ToArray());
        Assert.DoesNotContain(payload[0].Levels, l => l.Descriptor.StartsWith("Mức "));
    }

    /// <summary>Không khai mốc ⇒ hành vi Y HỆT trước: dải mặc định 0..maxScore.</summary>
    [Fact]
    public async Task Replace_WithoutLevels_FallsBackToDefaultBand()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await new RubricLibraryService(t.Db).ReplaceAsync(candidate, JobCategory.BE, OneCriterion(null));

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, "vi"));
        var payload = ScoringCriteriaBuilder.Build(loaded);

        Assert.Equal(6, payload[0].Levels.Count);   // 0..5
        Assert.All(payload[0].Levels, l => Assert.StartsWith("Mức ", l.Descriptor));
    }

    /// <summary>
    /// Thang méo → 400 (không phải 500). Luật là <c>CriterionLevelRules</c> DÙNG CHUNG với B2B, không
    /// phải một bản chép riêng — hai bản lệch nhau nghĩa là cùng một bộ mốc được nhận ở chỗ này và bị
    /// từ chối ở chỗ kia, mà triệu chứng duy nhất là điểm số trông vẫn hợp lý.
    /// </summary>
    [Theory]
    [InlineData("missing-zero")]
    [InlineData("missing-max")]
    [InlineData("too-short")]
    [InlineData("duplicate-score")]
    public async Task Replace_MalformedLevels_ThrowsInvalidOperation(string kind)
    {
        using var t = new TestDb();
        List<RubricLevelInput> bad = kind switch
        {
            "missing-zero" => [new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói đánh đổi."),
                               new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi.")],
            "missing-max" => [new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
                              new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói đánh đổi.")],
            "too-short" => [new(0, "ngắn"), new(5, "Nêu ý chính, có ví dụ và chỉ ra được đánh đổi.")],
            "duplicate-score" => [new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
                                  new(0, "Cũng không nêu được ý nào liên quan tới câu hỏi cả."),
                                  new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi.")],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RubricLibraryService(t.Db).ReplaceAsync(Guid.NewGuid(), JobCategory.BE, OneCriterion(bad)));
        Assert.Empty(await t.Db.RubricLevels.ToListAsync());
    }

    // ── (2) Clone từ bộ chuẩn ⇒ MỐC CỦA ADMIN đi theo ───────────────────────────────────────

    /// <summary>
    /// Người luyện chưa có rubric riêng ⇒ GET trả bộ chuẩn làm template, và mốc do admin soạn PHẢI đi
    /// kèm. Thiếu vế này thì bấm "tuỳ chỉnh" là bắt đầu từ trang trắng — chính là nghịch lý ở trên.
    /// </summary>
    [Fact]
    public async Task GetEffective_SeedTemplate_CarriesAdminAuthoredLevels()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        // Admin khai mốc cho bộ chuẩn.
        var admin = new AdminB2CRubricService(t.Db);
        var v1 = (await admin.GetAsync(JobCategory.BE, "vi"))!;
        await admin.ReplaceAsync(JobCategory.BE, new UpsertAdminRubricRequest(
            v1.Criteria.Select(c => new AdminRubricCriterionInput(c.Id, c.Description,
                Levels.Select(l => new AdminRubricLevelInput(l.Score, l.Descriptor)).ToList())).ToList()),
            "vi");

        var template = await new RubricLibraryService(t.Db)
            .GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE, "vi");

        Assert.False(template.IsCustom);
        Assert.Equal(7, template.Criteria.Count);
        Assert.All(template.Criteria, c => Assert.Equal(3, c.Levels.Count));
    }

    /// <summary>Vòng đọc → sửa → lưu giữ được mốc (đây là đường FE thật sự đi).</summary>
    [Fact]
    public async Task GetEffective_ThenReplaceEchoingLevels_KeepsThem()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();
        var admin = new AdminB2CRubricService(t.Db);
        var v1 = (await admin.GetAsync(JobCategory.FE, "vi"))!;
        await admin.ReplaceAsync(JobCategory.FE, new UpsertAdminRubricRequest(
            v1.Criteria.Select(c => new AdminRubricCriterionInput(c.Id, c.Description,
                Levels.Select(l => new AdminRubricLevelInput(l.Score, l.Descriptor)).ToList())).ToList()),
            "vi");

        var candidate = Guid.NewGuid();
        var svc = new RubricLibraryService(t.Db);
        var template = await svc.GetEffectiveAsync(candidate, JobCategory.FE, "vi");

        var saved = await svc.ReplaceAsync(candidate, JobCategory.FE, new UpsertRubricRequest(
            template.Criteria.Select(c => new RubricCriterionInput(
                c.Name, c.Description, c.Weight, c.MaxScore, c.Levels.ToList())).ToList()), "vi");

        Assert.True(saved.IsCustom);
        Assert.All(saved.Criteria, c => Assert.Equal(3, c.Levels.Count));
    }

    // ── (3) Nhãn nguồn thước ở màn kết quả ──────────────────────────────────────────────────

    /// <summary>
    /// Buổi chấm bằng rubric riêng ⇒ <c>rubricSource = Custom</c> kèm số phiên bản. Không có nhãn này
    /// thì người tự sửa tiêu chí cho lệch sẽ thấy điểm tụt và kết luận hệ thống chấm sai.
    /// </summary>
    [Fact]
    public async Task Result_CustomRubricSession_LabelsSourceAndVersion()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var custom = TestDb.Criterion(JobCategory.BE, version: 3, active: true,
            name: "Tự đặt", candidateId: candidate);
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.B2CRubricOwnerId = candidate;
        session.B2CRubricVersion = 3;
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(custom, session, q, answer);
        t.Db.AnswerScores.Add(new Entities.AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = answer.Id, CriterionId = custom.Id,
            AttemptNo = 1, Score = 4m, Reasoning = "x", RubricVersion = 3, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var result = await LoadResultAsync(t, session.Id, candidate);

        Assert.Equal("Custom", result.RubricSource);
        Assert.Equal(3, result.RubricVersion);
    }

    [Fact]
    public async Task Result_SystemRubricSession_LabelsSystemDefault()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var system = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.B2CRubricOwnerId = null;
        session.B2CRubricVersion = 1;
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(system, session, q, answer);
        t.Db.AnswerScores.Add(new Entities.AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = answer.Id, CriterionId = system.Id,
            AttemptNo = 1, Score = 4m, Reasoning = "x", RubricVersion = 1, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var result = await LoadResultAsync(t, session.Id, candidate);

        Assert.Equal("SystemDefault", result.RubricSource);
        Assert.Equal(1, result.RubricVersion);
    }

    /// <summary>
    /// Buổi CŨ (chưa ghim) ⇒ <c>null</c>, KHÔNG được vẽ thành "bộ chuẩn". Suy "biết" từ "không biết"
    /// là bịa — và ở đây nó sẽ nói dối đúng những người có rubric riêng từ trước (BK23).
    /// </summary>
    [Fact]
    public async Task Result_LegacySessionWithoutStamp_LeavesSourceNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var system = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.B2CRubricOwnerId = null;
        session.B2CRubricVersion = null;   // buổi có trước cặp cột ghim
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(system, session, q, answer);
        t.Db.AnswerScores.Add(new Entities.AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = answer.Id, CriterionId = system.Id,
            AttemptNo = 1, Score = 4m, Reasoning = "x", RubricVersion = 1, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var result = await LoadResultAsync(t, session.Id, candidate);

        Assert.Null(result.RubricSource);
        Assert.Null(result.RubricVersion);
    }

    private static async Task<SessionResultResponse> LoadResultAsync(TestDb t, Guid sessionId, Guid candidate)
    {
        var notifier = new Mock<Isas.InterviewService.Services.Interfaces.ISessionScoringNotifier>();
        var practice = new PracticeService(
            t.Db,
            new Mock<Isas.InterviewService.Services.Interfaces.IStorageService>().Object,
            new Mock<Isas.InterviewService.Services.Interfaces.IAiServiceQuestionGenerator>().Object,
            notifier.Object,
            new Mock<Isas.InterviewService.Services.Interfaces.ICreditReservationClient>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PracticeService>.Instance);

        var response = await practice.GetSessionAsync(candidate, sessionId)
            ?? throw new InvalidOperationException("Không đọc được buổi.");
        return response.Result ?? throw new InvalidOperationException("Buổi chưa có kết quả.");
    }
}
