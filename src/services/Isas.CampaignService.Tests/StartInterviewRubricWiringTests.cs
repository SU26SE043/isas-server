using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-16/18 — KHE NỐI giữa <c>StartInterviewAsync</c> và <see cref="ScoringCriteriaBuilder"/>.
///
/// <para><b>Vì sao có file này.</b> Mutation "đường tạo buổi thi tự map tay thay vì gọi builder" chạy
/// qua XANH toàn tập: các test Start hiện có đều mock <c>ICampaignSessionClient</c> bằng
/// <c>It.IsAny&lt;IReadOnlyList&lt;SessionCriterionInput&gt;&gt;()</c> nên KHÔNG nhìn vào nội dung bộ
/// tiêu chí được gửi đi. Nghĩa là mốc điểm có thể ngừng tới bộ chấm mà không test nào kêu — đúng
/// failure mode số 1 của cả tính năng: HR kiểm chứng thước A, ứng viên bị chấm bằng thước B, không
/// có triệu chứng nào vì cả hai đều vẫn ra điểm.</para>
/// </summary>
public class StartInterviewRubricWiringTests
{
    private static readonly Guid Candidate = Guid.NewGuid();

    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ nội dung";
    private const string D5 = "CÓ: nêu đủ ý kèm ví dụ | CÒN THIẾU: chưa nói đánh đổi";

    private static (Campaign Camp, List<CampaignCriterion> Criteria) Seed(CampaignTestDb tdb)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.RubricVersion = 4;
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId,
            QuestionText = "Giải thích DI?", Source = QuestionSource.CustomHr,
            IsRequired = true, CreatedAt = DateTime.UtcNow
        });

        var cr = new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Chuyên môn",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        camp.Criteria.Add(cr);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriterionLevels.AddRange(
            NewLevel(cr.Id, 5, D5), NewLevel(cr.Id, 0, D0));   // cố ý seed NGƯỢC thứ tự
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(camp.Id, Candidate));
        return (camp, new List<CampaignCriterion> { cr });
    }

    private static CampaignCriterionLevel NewLevel(Guid criterionId, int score, string descriptor)
        => new()
        {
            Id = Guid.NewGuid(), CriterionId = criterionId, Score = score, Descriptor = descriptor,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static Mock<ICampaignSessionClient> CapturingSession(
        Action<IReadOnlyList<SessionCriterionInput>, int> capture)
    {
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<SessionQuestionInput>?>(), It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, Guid _, string _, IReadOnlyList<string> _,
                    IReadOnlyList<SessionCriterionInput> criteria, DateTime? _, bool? _, int? _, int? _,
                    int? _, string _, int rubricVersion, IReadOnlyList<SessionQuestionInput>? _,
                    CampaignScoringPolicyInput? _, bool _, CancellationToken _)
                => capture(criteria, rubricVersion))
            .ReturnsAsync(new CampaignSessionResult(Guid.NewGuid(), new List<SessionQuestion>()));
        return m;
    }

    private static ParticipationService NewService(CampaignDbContext db, Mock<ICampaignSessionClient> session)
        => new(db, Mock.Of<IAuthProvisionClient>(), session.Object,
            NullLogger<ParticipationService>.Instance);

    // 🔴 Test đóng đúng khe mà mutation "map tay" lọt qua.
    [Fact]
    public async Task Start_gui_bo_tieu_chi_DUNG_bang_dau_ra_cua_ScoringCriteriaBuilder()
    {
        using var tdb = new CampaignTestDb();
        var (camp, _) = Seed(tdb);
        await tdb.Db.SaveChangesAsync();

        IReadOnlyList<SessionCriterionInput>? sent = null;
        var session = CapturingSession((c, _) => sent = c);

        await NewService(tdb.NewContext(), session).StartInterviewAsync(Candidate, camp.Id, default);

        Assert.NotNull(sent);

        // ⚠ Phải dựng bản kỳ vọng từ ĐÚNG dữ liệu đọc lên từ DB, không phải từ entity in-memory lúc
        // seed: decimal GIỮ SCALE và scale sống sót qua vòng round-trip khác nhau (1.0m in-memory vs
        // 1m đọc lại từ SQLite) ⇒ so với bản seed sẽ đỏ vì một khác biệt không tồn tại ở production.
        // Cùng gốc với lý do vân tay phải ghim "F4".
        using var read = tdb.NewContext();
        var fromDb = await read.CampaignCriteria
            .Include(c => c.Levels)
            .Where(c => c.CampaignId == camp.Id)
            .ToListAsync();

        Assert.Equal(Serialize(ScoringCriteriaBuilder.Build(fromDb)), Serialize(sent!));
    }

    // Vế cụ thể, để thông báo lỗi chỉ thẳng vào thứ bị mất khi ai đó bỏ builder.
    [Fact]
    public async Task Start_gui_kem_MOC_DIEM_sap_tang_dan()
    {
        using var tdb = new CampaignTestDb();
        var (camp, _) = Seed(tdb);
        await tdb.Db.SaveChangesAsync();

        IReadOnlyList<SessionCriterionInput>? sent = null;
        var session = CapturingSession((c, _) => sent = c);

        await NewService(tdb.NewContext(), session).StartInterviewAsync(Candidate, camp.Id, default);

        var levels = Assert.Single(sent!).Levels;
        Assert.Equal(new[] { 0, 5 }, levels.Select(l => l.Score));   // seed ngược, gửi đi phải xuôi
        Assert.Equal(D0, levels[0].Descriptor);
    }

    [Fact]
    public async Task Start_gui_rubricVersion_cua_chien_dich()
    {
        using var tdb = new CampaignTestDb();
        var (camp, _) = Seed(tdb);
        await tdb.Db.SaveChangesAsync();

        var version = 0;
        var session = CapturingSession((_, v) => version = v);

        await NewService(tdb.NewContext(), session).StartInterviewAsync(Candidate, camp.Id, default);

        Assert.Equal(4, version);   // Interview CHỈ CHÉP số này, không tự đánh số
    }

    private static string Serialize(IReadOnlyList<SessionCriterionInput> criteria)
        => JsonSerializer.Serialize(criteria, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
