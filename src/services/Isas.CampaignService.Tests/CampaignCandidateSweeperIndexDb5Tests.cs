using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Tests;

// DB5: StuckScreeningRepublisher (C15) quét campaign_candidates mỗi 2' theo predicate
// (Status, LastScreeningPublishedAt) — KHÔNG có campaign_id → index (campaign_id, status) vô dụng.
// Khoá bằng model-inspection: phải tồn tại 1 index composite (Status, LastScreeningPublishedAt)
// đúng thứ tự cột dẫn đầu = Status. CampaignTestDb.EnsureCreated() cũng sinh DDL thật (nếu index
// map sai cột → CREATE INDEX vỡ, constructor ném).
public class CampaignCandidateSweeperIndexDb5Tests
{
    [Fact]
    public void Model_co_index_status_lsp_dung_thu_tu()
    {
        using var tdb = new CampaignTestDb();   // EnsureCreated() → DDL của index chạy thật

        var entity = tdb.Db.Model.FindEntityType(typeof(CampaignCandidate));
        Assert.NotNull(entity);

        var expected = new[]
        {
            nameof(CampaignCandidate.Status),
            nameof(CampaignCandidate.LastScreeningPublishedAt)
        };

        var index = entity!.GetIndexes().SingleOrDefault(ix =>
            ix.Properties.Select(p => p.Name).SequenceEqual(expected));

        Assert.NotNull(index);                                  // index tồn tại đúng (Status, LastScreeningPublishedAt)
        Assert.False(index!.IsUnique);                          // non-unique (nhiều candidate cùng status)
        Assert.Equal("ix_campaign_candidates_status_lsp",
            index.GetDatabaseName());                           // tên rút gọn như migration
    }
}
