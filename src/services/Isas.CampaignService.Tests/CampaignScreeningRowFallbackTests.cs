using System.Data.Common;
using System.Text;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Sàng CV: DB từ chối MỘT dòng không được kéo cả lô đổ theo.
///
/// <para>Đo trên prod 21/09: một CV PDF có glyph không bảng Unicode ⇒ PdfPig trả <c>\0</c> ⇒ Postgres
/// <c>22021</c> ở INSERT ⇒ cả 4 CV đổ, <c>job_needs</c> vừa tốn một lượt AI cũng rollback, FE chỉ còn
/// "Không thể phân tích CV". Sanitizer ở bộ trích đã chặn <c>\0</c> tại nguồn; lớp này là lưới thứ hai
/// cho MỌI lý do DB từ chối một dòng.</para>
///
/// <para>SQLite không có <c>22021</c> ⇒ giả lập "DB từ chối" bằng <see cref="DbCommandInterceptor"/> ném
/// <see cref="SqliteException"/> khi câu INSERT mang tham số chứa dấu <c>&lt;&lt;BOOM&gt;&gt;</c> — cùng
/// hình dạng với lỗi thật (DbException bị EF bọc thành DbUpdateException), và lỗi biến mất đúng khi text
/// hỏng không còn trong dòng (dòng Rejected tối giản không mang <c>cv_parsed_text</c>).</para>
/// </summary>
public class CampaignScreeningRowFallbackTests
{
    private const string Boom = "<<BOOM>>";

    /// <summary>Ném khi bất kỳ tham số nào của câu ghi chứa <see cref="Boom"/> — đếm số lần ném.</summary>
    private sealed class RejectRowsContaining : DbCommandInterceptor
    {
        public int Thrown { get; private set; }

        private void Check(DbCommand command)
        {
            foreach (DbParameter p in command.Parameters)
            {
                if (p.Value is string s && s.Contains(Boom, StringComparison.Ordinal))
                {
                    Thrown++;
                    throw new SqliteException("SQLite Error: simulated 22021 invalid byte sequence", 1);
                }
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Check(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Check(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Check(command); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
    }

    private static CampaignSvc NewService(CampaignDbContext db, IEnumerable<string> parsedTexts, IJobNeedsSuggester? needs = null)
    {
        var parser = new Mock<IParserService>();
        var seq = parser.SetupSequence(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()));
        foreach (var t in parsedTexts)
            seq = seq.ReturnsAsync(new ParseResult { RawText = t });

        return new CampaignSvc(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), parser.Object,
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), jobNeedsSuggester: needs);
    }

    private static IFormFile Pdf(string fileName)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("xxxxxxxx"));
        return new FormFile(stream, 0, stream.Length, "files", fileName) { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }

    private static IFormFileCollection Files(params IFormFile[] files)
    {
        var col = new FormFileCollection();
        col.AddRange(files);
        return col;
    }

    private static Campaign Seed(CampaignTestDb tdb, Guid owner, bool withJobNeeds)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.JDText = "JD backend PHP/MySQL";
        if (withJobNeeds)
            camp.JobNeeds = new List<JobNeed> { new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "PHP", Source = JobNeedSources.HrEdited } };
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    // ── 1. 1 dòng bị DB từ chối trong lô 3 ⇒ 2 dòng Filtered nguyên vẹn + dòng hỏng thành Rejected ──
    [Fact]
    public async Task MotDongBiTuChoi_HaiDongKiaVanLuu_DongHongThanhRejected()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = Seed(tdb, owner, withJobNeeds: true);
        var reject = new RejectRowsContaining();
        var svc = NewService(tdb.NewContext(reject), new[] { "CV một a@x.com", $"CV hai {Boom} b@x.com", "CV ba c@x.com" });

        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("a.pdf"), Pdf("b.pdf"), Pdf("c.pdf")), default);

        Assert.Equal(3, res.Received);
        Assert.Equal(2, res.Filtered);
        Assert.Equal(1, res.Rejected);
        Assert.True(reject.Thrown >= 2, $"phải ném ở lô rồi ném lại ở dòng lẻ (thrown={reject.Thrown})");

        using var check = tdb.NewContext();
        var rows = await check.CvSubmissions.Where(c => c.CampaignId == camp.Id).OrderBy(c => c.CreatedAt).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Status == CvSubmissionStatus.Filtered && r.CvParsedText != null));

        var bad = Assert.Single(rows, r => r.Status == CvSubmissionStatus.Rejected);
        Assert.Equal("CV không đọc được — upload lại.", bad.RejectReason);
        Assert.Equal(CvParseStatus.Failed, bad.ParseStatus);
        Assert.Null(bad.CvParsedText);           // text hỏng KHÔNG được ghi lại
        Assert.NotNull(bad.CvFileUrl);           // file gốc vẫn tải được cho HR
        Assert.Contains(res.Candidates, c => c.Id == bad.Id && c.Status == "Rejected");

        var audit = await check.AuditLogs.Where(a => a.EntityId == camp.Id && a.Action == AuditAction.ScreenCandidates).ToListAsync();
        var one = Assert.Single(audit);          // audit của lô hỏng KHÔNG được ghi hai lần
        Assert.Contains("2 qua, 1 loại", one.Summary);
        Assert.Contains("1 dòng bị DB từ chối", one.Summary);
    }

    // ── 2. job_needs vừa rút (SCR1 lazy) phải xuống DB dù lô CV bị từ chối — không mất lượt AI ──
    [Fact]
    public async Task LoBiTuChoi_JobNeedsVuaRutVanDuocLuu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = Seed(tdb, owner, withJobNeeds: false);
        var needs = new Mock<IJobNeedsSuggester>();
        needs.Setup(n => n.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SuggestedJobNeed> { new(JobNeedCategories.Technical, "PHP 4 năm") });
        var svc = NewService(tdb.NewContext(new RejectRowsContaining()), new[] { $"CV hỏng {Boom} a@x.com" }, needs.Object);

        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("a.pdf")), default);

        Assert.Equal(1, res.Rejected);
        using var check = tdb.NewContext();
        var saved = await check.Campaigns.SingleAsync(c => c.Id == camp.Id);
        Assert.NotNull(saved.JobNeeds);
        Assert.Contains(saved.JobNeeds!, n => n.Text == "PHP 4 năm");
        needs.Verify(n => n.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 3. Không có dòng nào bị từ chối ⇒ đúng MỘT audit, không có hậu tố "bị DB từ chối" ──
    [Fact]
    public async Task LoSach_MotAudit_KhongCoHauTo()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = Seed(tdb, owner, withJobNeeds: true);
        var reject = new RejectRowsContaining();
        var svc = NewService(tdb.NewContext(reject), new[] { "CV một a@x.com", "CV hai b@x.com" });

        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("a.pdf"), Pdf("b.pdf")), default);

        Assert.Equal(2, res.Filtered);
        Assert.Equal(0, reject.Thrown);
        using var check = tdb.NewContext();
        var audit = Assert.Single(await check.AuditLogs.Where(a => a.EntityId == camp.Id).ToListAsync());
        Assert.DoesNotContain("bị DB từ chối", audit.Summary);
    }
}
