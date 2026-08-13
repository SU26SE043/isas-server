using Isas.CampaignService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CampaignDbContext chạy trên SQLite in-memory (giữ connection mở để DB sống).
/// Seed entity với Id/CreatedAt set SẴN — tránh default Postgres (gen_random_uuid/now) không có trên SQLite.
/// </summary>
public sealed class CampaignTestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public CampaignDbContext Db { get; }

    // Connection dùng chung để BackgroundService (StuckScreeningRepublisher) tạo scope DbContext riêng.
    public SqliteConnection Connection => _conn;

    public CampaignTestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    /// <param name="interceptors">
    /// Tuỳ chọn (mặc định rỗng ⇒ mọi call-site `NewContext()` hiện có không đổi). Dùng để gắn
    /// <c>ISaveChangesInterceptor</c>/<c>DbCommandInterceptor</c> khi cần chứng minh một đường đi
    /// KHÔNG ghi gì — assert "bảng vẫn còn N dòng" chỉ chứng minh *kết quả* giống nhau, không chứng
    /// minh *không có lượt ghi nào xảy ra*.
    /// </param>
    public CampaignDbContext NewContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<CampaignDbContext>()
            .AddInterceptors(interceptors)
            .UseSqlite(_conn)
            // DB2b — UseSnakeCaseNamingConvention() để cột SQLite mang tên snake_case, khớp partial index
            // model-level `HasFilter("published_at IS NULL")` trên outbox_messages (raw SQL cột snake_case).
            // Không bật → EnsureCreated sinh CREATE INDEX tham chiếu cột không tồn tại → vỡ toàn bộ test
            // (precedent DB19 Interview với CHECK constraint). Prod đã snake_case (Program.cs).
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CampaignDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }

    public static Campaign NewCampaign(
        Guid orgId, CampaignStatus status = CampaignStatus.Draft, bool antiCheat = true)
        => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Title = "Test Campaign",
            Status = status,
            AntiCheatEnabled = antiCheat,
            StartsAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    // DB16 — membership (D2 join) tách khỏi bảng God (nay cv_submission). CvSubmissionId null = đường-1.
    public static CampaignMembership NewMembership(
        Guid campaignId, Guid candidateId,
        MembershipStatus status = MembershipStatus.Joined,
        Guid? cvSubmissionId = null,
        Guid? sessionId = null,
        InterviewProgressStatus? interviewStatus = null,
        string? referenceImageKey = null)
        => new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            CvSubmissionId = cvSubmissionId,
            Status = status,
            JoinedAt = DateTime.UtcNow,
            SessionId = sessionId,
            InterviewStatus = interviewStatus,
            ReferenceImageKey = referenceImageKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
}
