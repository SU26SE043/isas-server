using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// ADP1 (BE-3) — nhãn CÁCH GỘP ĐIỂM trên bảng xếp hạng
/// (<c>campaign_rankings.score_aggregation_version</c>), đến QUA event <c>SessionScored</c>.
///
/// <para><b>Vì sao:</b> BE-2 đổi phép gộp từ "mỗi answer một phiếu" sang "mỗi CÂU GỐC một phiếu" ⇒ hai
/// thang KHÔNG so sánh được (cùng một buổi: cũ <b>34.00</b>, mới <b>25.00</b>). Bảng này CHÍNH LÀ chỗ
/// CAMP-10 đem điểm mọi ứng viên trong campaign so THẲNG với nhau ⇒ chiến dịch đang tuyển vắt qua lần
/// deploy sẽ có ứng viên ở cả hai thang nằm chung một bảng, không dấu hiệu gì.</para>
///
/// <para><b>Bất biến trung tâm — và nó KHÔNG phải "luôn có nhãn":</b> <c>null</c> = KHÔNG BIẾT, và
/// tuyệt đối không được vẽ thành <c>1</c> (BK23 — suy "biết" từ "không biết" là bịa). Khuyết nhãn là
/// chấp nhận được; nhãn SAI thì trả lời "hai điểm này cùng thước đo không?" một cách sai mà tự tin, và
/// không ai có cách nào phát hiện. Mẫu y hệt <c>RubricVersion</c> ngay bên cạnh.</para>
///
/// <para>⚠ B10 đã cắn ĐÚNG SỢI DÂY NÀY: Interview phát <c>ScoreFallback</c> mà DTO Campaign không khai
/// property ⇒ System.Text.Json bỏ qua khoá lạ ⇒ cờ MẤT, không lỗi không log. Nên ở đây tên khoá được
/// khoá bằng chuỗi NGUYÊN VĂN, không chỉ bằng property.</para>
/// </summary>
public class RankingScoreAggregationVersionAdp1Tests
{
    // Khoá JSON trên dây, viết nguyên văn — cặp đối xứng của
    // Isas.InterviewService.Tests.ScoreAggregationVersionStampAdp1Tests.WireKey.
    // Đổi tên property một bên mà quên bên kia: cả hai đầu vẫn tự-nhất-quán và tự-xanh (serialize và
    // deserialize đều dùng chính DTO của mình) — chỉ hai chuỗi literal này bắt được.
    private const string WireKey = "ScoreAggregationVersion";

    // Đúng options mà SessionScoredConsumer dùng thật (PropertyNameCaseInsensitive = true). Dựng event
    // bằng `new SessionScoredMessage{...}` sẽ bỏ qua toàn bộ tầng JSON — tức bỏ qua đúng tầng đã hỏng
    // ở B10; nên các ca "event cũ" ở dưới đi qua deserialize thật.
    private static readonly JsonSerializerOptions ConsumerOptions = new() { PropertyNameCaseInsensitive = true };

    private static RankingEventHandler NewHandler(CampaignDbContext db)
        => new(db, Mock.Of<ILogger<RankingEventHandler>>());

    private static async Task<Campaign> SeedCampaignAsync(CampaignTestDb tdb)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        return camp;
    }

    private static async Task<CampaignRanking> SeedRankingAsync(
        CampaignTestDb tdb, Guid campaignId, Guid sessionId, int? aggregationVersion, decimal score = 34m)
    {
        var row = new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = Guid.NewGuid(),
            SessionId = sessionId, TotalScore = score,
            ScoreAggregationVersion = aggregationVersion, UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CampaignRankings.Add(row);
        await tdb.Db.SaveChangesAsync();
        return row;
    }

    private static async Task<CampaignRanking> ReadAsync(CampaignTestDb tdb, Guid sessionId)
        => await tdb.NewContext().CampaignRankings.AsNoTracking().FirstAsync(r => r.SessionId == sessionId);

    // Event "bản Interview CŨ": dựng JSON KHÔNG hề có khoá ADP1, rồi deserialize như consumer thật.
    private static SessionScoredMessage OldEventWithoutKey(Guid campaignId, Guid sessionId, decimal score = 34m)
    {
        var json = JsonSerializer.Serialize(new
        {
            sessionId, campaignId, candidateId = Guid.NewGuid(), totalScore = score, scoredAt = DateTime.UtcNow
        });
        Assert.DoesNotContain(WireKey, json);   // tiền đề của cả ca: khoá thật sự VẮNG
        return JsonSerializer.Deserialize<SessionScoredMessage>(json, ConsumerOptions)!;
    }

    // ── (1) Nhánh TẠO MỚI ───────────────────────────────────────────────────────

    [Fact]
    public async Task TaoMoi_EventMangNhan_ThiLuuLai()
    {
        using var tdb = new CampaignTestDb();
        var camp = await SeedCampaignAsync(tdb);
        var sessionId = Guid.NewGuid();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId, CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            TotalScore = 25m, ScoredAt = DateTime.UtcNow, ScoreAggregationVersion = 2
        }, default);

        Assert.Equal(2, (await ReadAsync(tdb, sessionId)).ScoreAggregationVersion);
    }

    // Bản Interview CŨ không gửi field (hai service deploy không nguyên tử) ⇒ phải nhận NULL và KHÔNG
    // vỡ. Ca này xảy ra THẬT trong cửa sổ giữa hai lần khởi động, và cả với event còn tồn trong outbox
    // lúc deploy.
    [Fact]
    public async Task TaoMoi_EventCuThieuKhoa_ThiNull_KhongPhai1()
    {
        using var tdb = new CampaignTestDb();
        var camp = await SeedCampaignAsync(tdb);
        var evt = OldEventWithoutKey(camp.Id, Guid.NewGuid());

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt, default);

        var row = await ReadAsync(tdb, evt.SessionId);
        Assert.Null(row.ScoreAggregationVersion);   // "không biết" — KHÔNG được vẽ thành 1 (BK23)
        Assert.Equal(34m, row.TotalScore);          // và phần còn lại vẫn ghi bình thường
    }

    // ── (2) Nhánh UPSERT (outbox at-least-once ⇒ bàn giao lại là chuyện THƯỜNG) ──

    [Fact]
    public async Task Upsert_EventMangNhan_ThiGhiDeLenDongDaCo()
    {
        using var tdb = new CampaignTestDb();
        var camp = await SeedCampaignAsync(tdb);
        var sessionId = Guid.NewGuid();
        await SeedRankingAsync(tdb, camp.Id, sessionId, aggregationVersion: null);

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId, CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            TotalScore = 25m, ScoredAt = DateTime.UtcNow, ScoreAggregationVersion = 2
        }, default);

        Assert.Equal(2, (await ReadAsync(tdb, sessionId)).ScoreAggregationVersion);
        Assert.Single(await tdb.NewContext().CampaignRankings.Where(r => r.SessionId == sessionId).ToListAsync());
    }

    /// <summary>
    /// ⚠ <b>QUYẾT ĐỊNH ĐƯỢC KHOÁ TƯỜNG MINH — đọc trước khi đổi.</b>
    ///
    /// <para>Một event bàn giao lại MUỘN từ bản Interview CŨ (thiếu khoá ⇒ <c>null</c>) <b>ĐƯỢC PHÉP
    /// xoá</b> nhãn đã đúng trên dòng. Đây là lựa chọn có chủ đích, không phải sơ suất:</para>
    ///
    /// <list type="number">
    /// <item><b>Nhất quán với chính dòng đó.</b> Cùng một event cũng đè lại <c>TotalScore</c>,
    /// <c>RubricVersion</c>, <c>ScoringInputs</c>, <c>PolicyVersion</c>. Giữ riêng nhãn ADP1 lại tạo ra
    /// một dòng LAI: điểm của lượt chấm này, nhãn của lượt chấm khác — đúng thứ nói dối tệ nhất, vì nó
    /// khẳng định hai con số cùng thước đo trong khi chúng đến từ hai lượt khác nhau.</item>
    ///
    /// <item><b>Event là nguồn quyền lực.</b> Nếu người gửi không biết mình gộp kiểu gì thì Campaign
    /// cũng không biết. Suy ra "chắc vẫn là 2" từ dòng cũ chính là suy "biết" từ "không biết" (BK23),
    /// chỉ khác chỗ đứng.</item>
    ///
    /// <item><b>Cửa sổ tồn tại của ca này là hữu hạn</b> — chỉ trong lúc rollout, khi một Interview cũ
    /// còn đang chạy hoặc event cũ còn tồn trong outbox. Sau đó mọi event đều mang nhãn.</item>
    /// </list>
    ///
    /// <para>Hướng ngược lại (<c>existing.X = evt.X ?? existing.X</c>, giữ nhãn cũ) là một lựa chọn
    /// hợp lệ về nghiệp vụ. Nhưng nó phải được chọn CÓ Ý THỨC, cùng lúc với 4 field kia — đổi một
    /// mình dòng ADP1 sẽ đẻ ra đúng dòng LAI ở mục (1). Test này tồn tại để lần đổi đó không xảy ra
    /// một cách vô tình.</para>
    /// </summary>
    [Fact]
    public async Task Upsert_EventCuThieuKhoa_XOA_nhan_DaDung_ThayVi_GiuLai()
    {
        using var tdb = new CampaignTestDb();
        var camp = await SeedCampaignAsync(tdb);
        var sessionId = Guid.NewGuid();
        await SeedRankingAsync(tdb, camp.Id, sessionId, aggregationVersion: 2, score: 25m);

        var evt = OldEventWithoutKey(camp.Id, sessionId, score: 34m);
        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt, default);

        var row = await ReadAsync(tdb, sessionId);
        Assert.Null(row.ScoreAggregationVersion);   // nhãn đi theo điểm...
        Assert.Equal(34m, row.TotalScore);          // ...và điểm cũng đã bị đè — dòng KHÔNG lai
    }

    // ── (3) Hợp đồng WIRE — nửa Campaign của cặp guard chống B10 ────────────────

    // Khoá đúng cái đã hỏng ở B10: khoá JSON Interview phát ra phải RƠI VÀO property của lớp này.
    // Payload dựng nguyên văn PascalCase — đúng dạng OutboxMessage.ForScored sinh ra (JsonSerializer
    // .Serialize không truyền options ⇒ giữ nguyên tên property).
    [Fact]
    public void Wire_KhoaJsonCuaInterview_RoiDungVaoProperty()
    {
        var json = $$"""
        {"SessionId":"{{Guid.Empty}}","CampaignId":null,"CandidateId":"{{Guid.Empty}}",
         "TotalScore":25,"ScoredAt":"2026-01-01T00:00:00Z","{{WireKey}}":2}
        """;

        var evt = JsonSerializer.Deserialize<SessionScoredMessage>(json, ConsumerOptions)!;

        Assert.Equal(2, evt.ScoreAggregationVersion);
    }

    // Khoá VẮNG ⇒ null, và deserialize KHÔNG được ném: event cũ còn nằm trong outbox lúc deploy phải
    // đi qua được, nếu không thì nack-requeue vô hạn (poison message chặn cả queue — mẫu DB22).
    [Fact]
    public void Wire_ThieuKhoa_ThiNull_KhongNem()
    {
        var json = $$"""
        {"SessionId":"{{Guid.Empty}}","CampaignId":null,"CandidateId":"{{Guid.Empty}}",
         "TotalScore":25,"ScoredAt":"2026-01-01T00:00:00Z"}
        """;

        var evt = JsonSerializer.Deserialize<SessionScoredMessage>(json, ConsumerOptions)!;

        Assert.Null(evt.ScoreAggregationVersion);
    }

    // ── (4) Không hồi quy: nhãn mới không đè lên nhãn thước đo đã có ────────────

    // RubricVersion (CAMP-18) · PolicyVersion (SCP1) · ScoreAggregationVersion (ADP1) cùng nằm trên một
    // dòng và cùng trả lời "thước đo nào". Thêm cột thứ ba vào đúng đường ghi đó là chỗ dễ đụng nhất.
    [Fact]
    public async Task NhanMoi_KhongDeLenRubricVersionVaPolicyVersion()
    {
        using var tdb = new CampaignTestDb();
        var camp = await SeedCampaignAsync(tdb);
        var sessionId = Guid.NewGuid();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId, CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            TotalScore = 25m, ScoredAt = DateTime.UtcNow,
            RubricVersion = 7, CampaignPolicyVersion = null, ScoreAggregationVersion = 2
        }, default);

        var row = await ReadAsync(tdb, sessionId);
        Assert.Equal(7, row.RubricVersion);
        Assert.Null(row.PolicyVersion);
        Assert.Equal(2, row.ScoreAggregationVersion);
    }

    // B2C (campaign_id = null) không vào bảng xếp hạng — nhãn ADP1 không được làm đổi điều đó.
    [Fact]
    public async Task B2C_VanKhongTaoDongXepHang_DuCoNhan()
    {
        using var tdb = new CampaignTestDb();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = Guid.NewGuid(), CampaignId = null, CandidateId = Guid.NewGuid(),
            TotalScore = 25m, ScoredAt = DateTime.UtcNow, ScoreAggregationVersion = 2
        }, default);

        Assert.Empty(await tdb.NewContext().CampaignRankings.ToListAsync());
    }
}
