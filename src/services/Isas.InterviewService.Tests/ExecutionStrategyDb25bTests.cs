using System.Reflection;
using System.Runtime.CompilerServices;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.Entities;   // OutboxMessage
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB25b — hàng rào cho một lớp lỗi CHỈ NỔ TRÊN POSTGRES.
///
/// <para><b>Vế 1 — "phải bọc":</b> <c>Program.cs</c> bật <c>EnableRetryOnFailure()</c>, mà chiến lược
/// retry của EF từ chối transaction do người dùng tự mở. SQLite mặc định chạy chiến lược KHÔNG-retry
/// nên toàn bộ suite hiện tại MÙ với ràng buộc này. Các test dưới cấu hình SQLite bằng chiến lược CÓ
/// retry để code sai ĐỎ ngay ở CI thay vì lúc deploy.</para>
///
/// <para><b>Vế 2 — "bọc thôi chưa đủ":</b> retry chạy lại delegate nhưng EF KHÔNG reset change
/// tracker. Entity <c>Add()</c> bên trong transaction sẽ bị chèn HAI LẦN ở lần thử sau. Các test
/// "retry THẬT" dưới đây ép hỏng đúng một lần rồi đếm số dòng thực tế trong DB.</para>
/// </summary>
public class ExecutionStrategyDb25bTests
{
    // ── Vế 1: chạy được dưới chiến lược CÓ retry ──────────────────────────

    [Fact]
    public async Task PromptUpsert_ChayDuocDuoiChienLuocCoRetry()
    {
        using var t = new TestDb();
        using var db = t.NewContext(deps => new RetryingStrategyStub(deps), null);
        var svc = new PromptTemplateService(db, NullLogger<PromptTemplateService>.Instance);

        var res = await svc.UpsertAsync(
            PromptTemplateKeys.ScoringPersona, "bản một", Guid.NewGuid(), null, default);

        Assert.Equal(1, res.Version);
        using var verify = t.NewContext();
        Assert.Single(verify.PromptTemplates);
    }

    [Fact]
    public async Task Sweeper_ChayDuocDuoiChienLuocCoRetry()
    {
        using var t = new TestDb();
        var session = SeedExpiredSession(t);

        await ScanOnce(BuildSweeper(t, deps => new RetryingStrategyStub(deps)));

        using var verify = t.NewContext();
        Assert.Equal(SessionStatus.SessionAbandoned, verify.PracticeSessions.Single().Status);
        Assert.Equal(1, TestDb.OutboxCount(verify, session, OutboxMessage.SessionAbandonedType));
    }

    // ── Vế 2: retry THẬT không được nhân đôi dòng ─────────────────────────

    /// <summary>
    /// Sự cố hỏng ở đúng câu INSERT outbox, đúng MỘT lần → strategy chạy lại delegate.
    /// Nếu delegate không dọn change tracker, <c>OutboxMessage</c> của lần thử đầu còn kẹt ở
    /// <c>Added</c> ⇒ lần thử sau chèn CẢ HAI ⇒ hai sự kiện <c>session.abandoned</c> cho một buổi.
    /// </summary>
    [Fact]
    public async Task Sweeper_RetryThat_ChiGhiDungMotOutboxRow()
    {
        using var t = new TestDb();
        var session = SeedExpiredSession(t);
        var fault = new ThrowOnceInterceptor("outbox_messages");

        await ScanOnce(BuildSweeper(t, deps => new RetryOnTestFaultStrategy(deps), fault));

        Assert.True(fault.Fired, "Interceptor chưa hề kích hoạt ⇒ phép thử không chứng minh được gì.");
        using var verify = t.NewContext();
        Assert.Equal(SessionStatus.SessionAbandoned, verify.PracticeSessions.Single().Status);
        Assert.Equal(1, TestDb.OutboxCount(verify, session, OutboxMessage.SessionAbandonedType));
    }

    /// <summary>Cùng lý lẽ cho registry prompt: retry không được đẻ ra hai bản cùng version.</summary>
    [Fact]
    public async Task PromptUpsert_RetryThat_ChiGhiDungMotBanMoi()
    {
        using var t = new TestDb();
        var fault = new ThrowOnceInterceptor("prompt_templates");
        using var db = t.NewContext(deps => new RetryOnTestFaultStrategy(deps), new[] { fault });
        var svc = new PromptTemplateService(db, NullLogger<PromptTemplateService>.Instance);

        await svc.UpsertAsync(PromptTemplateKeys.ScoringPersona, "bản một", Guid.NewGuid(), null, default);

        Assert.True(fault.Fired, "Interceptor chưa hề kích hoạt ⇒ phép thử không chứng minh được gì.");
        using var verify = t.NewContext();
        Assert.Single(verify.PromptTemplates);
    }

    // ── Vế 3: guard cấu trúc cho MỌI site về sau ──────────────────────────

    /// <summary>
    /// Hai test hành vi ở trên chỉ phủ hai site đang có. Guard này đọc thẳng mã nguồn để bắt site
    /// MỚI: bất kỳ <c>BeginTransactionAsync</c> nào cũng phải nằm trong một khối
    /// <c>DbRetry.RunAsync</c>. Không có nó thì người thêm transaction thứ ba sẽ ship một
    /// <c>InvalidOperationException</c> ở mọi request Postgres mà CI vẫn xanh.
    /// </summary>
    [Fact]
    public void MoiTransactionTuMo_DeuNamTrongDbRetry()
    {
        var offenders = TransactionSiteScanner.FindUnwrapped(
            Path.Combine(RepoRoot(), "src", "services", "Isas.InterviewService"));

        Assert.True(offenders.Count == 0,
            "BeginTransactionAsync KHÔNG nằm trong DbRetry.RunAsync (sẽ ném trên Postgres):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Nửa còn lại của DB25b: bọc transaction mà KHÔNG bật retry thì cả task này vô nghĩa. Không test
    /// nào khác chạm tới <c>Program.cs</c> (suite chạy SQLite, tự dựng DbContext), nên gỡ
    /// <c>EnableRetryOnFailure</c> sẽ trôi qua toàn bộ CI trong im lặng. Đọc thẳng mã nguồn là cách
    /// duy nhất khoá được nó ở tầng unit test.
    /// </summary>
    [Fact]
    public void ProgramCs_BatEnableRetryOnFailure()
    {
        var program = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "services", "Isas.InterviewService", "Program.cs"));

        Assert.Contains("EnableRetryOnFailure", program);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static Guid SeedExpiredSession(TestDb t)
    {
        var s = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            campaignId: Guid.NewGuid(), deadline: DateTime.UtcNow.AddMinutes(-5));
        t.Db.Add(s);
        t.Db.SaveChanges();
        return s.Id;
    }

    private static SessionAbandonSweeper BuildSweeper(
        TestDb t,
        Func<ExecutionStrategyDependencies, IExecutionStrategy> strategy,
        IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
        {
            o.UseSqlite(t.Connection, x => x.ExecutionStrategy(strategy)).UseSnakeCaseNamingConvention();
            if (interceptor is not null) o.AddInterceptors(interceptor);
        });
        var provider = services.BuildServiceProvider();

        return new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance);
    }

    private static async Task ScanOnce(SessionAbandonSweeper s)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }

    // Neo theo đường dẫn file NGUỒN lúc biên dịch — không phụ thuộc thư mục làm việc của test runner.
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    /// <summary>Chiến lược CÓ <c>RetriesOnFailure</c> nhưng không thử lại thật — chỉ để bật đúng ràng
    /// buộc "không cho tự mở transaction" của EF (mẫu <c>AccountCreationTransactionTests</c>).</summary>
    private sealed class RetryingStrategyStub(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        public override bool RetriesOnFailure => true;
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
