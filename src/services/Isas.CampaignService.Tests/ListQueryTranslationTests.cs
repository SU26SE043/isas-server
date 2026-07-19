using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Tests;

/// <summary>
/// HỢP ĐỒNG DỊCH SQL cho 3 danh sách vừa được keyset-paged + đẩy filter/sort xuống SQL.
/// Test còn lại chạy trên SQLite — SQLite dịch được KHÔNG chứng minh Npgsql cũng dịch được, mà đúng
/// Npgsql mới là provider chạy thật. Nếu một vị ngữ nào đó rơi về client-eval trên Postgres thì
/// EF sẽ ném lúc chạy (hoặc, tệ hơn, phân trang chạy trên tập đã materialize) — mà KHÔNG test nào
/// khác đỏ. Đây là test bắt đúng ca đó, theo tiền lệ DB27 (SweeperIndexTests bên Interview).
/// Provider Npgsql chỉ dùng để SINH SQL (`ToQueryString`), không mở kết nối nào.
/// </summary>
public class ListQueryTranslationTests
{
    private static CampaignDbContext NpgsqlProbe() =>
        new(new DbContextOptionsBuilder<CampaignDbContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=x;Password=y")
            .UseSnakeCaseNamingConvention()
            .Options);

    // (1) 🔑 Shortlist sort=score — gọi ĐÚNG hàm production (CvScreeningService.ApplyScoreOrder/
    // ApplyScoreKeyset) rồi soi SQL Npgsql sinh ra. Phải gọi hàm thật vì đây là ca mà test hành vi
    // trên SQLite KHÔNG BAO GIỜ bắt được: Postgres coi NULL là LỚN NHẤT (ORDER BY … DESC ⇒ ứng viên
    // chưa chấm nhảy lên ĐẦU shortlist) còn SQLite coi NULL nhỏ nhất (xuống cuối, đúng ý). Bỏ COALESCE
    // đi thì mọi test hành vi vẫn xanh còn production thì sai — test này là cái duy nhất đỏ.
    [Fact]
    public void Candidates_ScoreOrder_LuonCoalesce_DeKhongPhuThuocViTriNullCuaProvider()
    {
        using var db = NpgsqlProbe();

        var orderSql = CvScreeningService.ApplyScoreOrder(db.CvSubmissions).Take(20).ToQueryString();

        Assert.Contains("COALESCE", orderSql, StringComparison.OrdinalIgnoreCase);
        // COALESCE phải nằm trong ORDER BY (không chỉ đâu đó trong SELECT).
        var orderByPart = orderSql[orderSql.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase)..];
        Assert.Contains("COALESCE", orderByPart, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", orderByPart);
        Assert.Contains("LIMIT", orderSql, StringComparison.OrdinalIgnoreCase);

        // Predicate keyset phải dùng CÙNG biểu thức khoá với ORDER BY, nếu không phân trang trượt dòng.
        var keysetSql = CvScreeningService
            .ApplyScoreKeyset(db.CvSubmissions, 70, Guid.NewGuid())
            .ToQueryString();
        Assert.Contains("COALESCE", keysetSql, StringComparison.OrdinalIgnoreCase);
        // Guid tie-break phải thành so sánh uuid trong SQL, KHÔNG phải gọi CompareTo phía client.
        Assert.DoesNotContain("CompareTo", keysetSql);
    }

    // (2) Shortlist sort=name — khoá là lower(coalesce(full_name,'')); dùng chung biểu thức ở predicate
    // và ORDER BY để không lệ thuộc collation mặc định (Postgres locale-aware vs SQLite BINARY).
    [Fact]
    public void Candidates_NameKeyset_DichSangSql_Lower()
    {
        using var db = NpgsqlProbe();
        var campaignId = Guid.NewGuid();
        var curId = Guid.NewGuid();
        var curKey = "nguyen";

        var sql = db.CvSubmissions
            .Where(c => c.CampaignId == campaignId)
            .Where(c => string.Compare((c.FullName ?? string.Empty).ToLower(), curKey) > 0
                || ((c.FullName ?? string.Empty).ToLower() == curKey && c.Id.CompareTo(curId) > 0))
            .OrderBy(c => (c.FullName ?? string.Empty).ToLower())
            .ThenBy(c => c.Id)
            .Take(20)
            .ToQueryString();

        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CompareTo", sql);
    }

    // (3) search — case-insensitive khớp tên HOẶC email, phải nằm trong WHERE của SQL chứ không
    // phải lọc sau khi đã kéo cả bảng về (đây chính là bug hình dạng cũ của endpoint này).
    [Fact]
    public void Candidates_Search_DichSangSql_KhongLocPhiaClient()
    {
        using var db = NpgsqlProbe();
        var campaignId = Guid.NewGuid();
        var needle = "an";

        var sql = db.CvSubmissions
            .Where(c => c.CampaignId == campaignId)
            .Where(c => (c.FullName != null && c.FullName.ToLower().Contains(needle))
                     || (c.Email != null && c.Email.ToLower().Contains(needle)))
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full_name", sql);
        Assert.Contains("email", sql);
    }

    // (4) Invitations — trạng thái suy read-time nay ĐẨY XUỐNG SQL: nhánh "đã join" là EXISTS trên
    // campaign_membership (2 đường ghép: cv_submission_id hoặc email). Nếu cái này client-eval thì
    // ?status=Joined sẽ lọc trong phạm vi 1 trang ⇒ kết quả sai mà không có gì báo.
    [Fact]
    public void Invitations_JoinedPredicate_DichSangSql_Exists()
    {
        using var db = NpgsqlProbe();
        var campaignId = Guid.NewGuid();

        var sql = db.CampaignInvitations
            .Where(i => i.CampaignId == campaignId)
            .Where(i => i.RevokedAt == null)
            .Where(i => db.CampaignMemberships.Any(m => m.CampaignId == campaignId
                && ((i.CampaignCandidateId != null && m.CvSubmissionId == i.CampaignCandidateId)
                    || (m.Email != null && m.Email.Trim().ToLower() == i.Email.Trim().ToLower()))))
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Take(20)
            .ToQueryString();

        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("campaign_membership", sql);
        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    // (5) my-campaigns — soft-delete campaign (D11) phải bị loại NGAY TRONG SQL, TRƯỚC LIMIT, nếu không
    // campaign đã xoá sẽ chiếm chỗ trong trang rồi mới bị bỏ ở C# ⇒ trang ngắn oan. Ở đây việc đó do
    // global query filter DB13 trên CampaignMembership lo (`x.Campaign.DeletedAt == null`) — service
    // KHÔNG tự thêm vị ngữ nào. Test này khoá đúng chỗ đó: nếu ai gỡ query filter DB13 thì `deleted_at`
    // biến mất khỏi SQL và test đỏ.
    [Fact]
    public void MyCampaigns_SoftDeleteFilter_NamTrongSql_TruocLimit()
    {
        using var db = NpgsqlProbe();
        var candidateId = Guid.NewGuid();

        var sql = db.CampaignMemberships
            .Where(m => m.CandidateId == candidateId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(20)
            .ToQueryString();

        Assert.Contains("deleted_at", sql);
        var limitIdx = sql.LastIndexOf("LIMIT", StringComparison.OrdinalIgnoreCase);
        Assert.True(limitIdx > 0, "Phải có LIMIT (phân trang ở DB, không phải trong bộ nhớ).");
        Assert.True(sql.IndexOf("deleted_at", StringComparison.Ordinal) < limitIdx,
            "Vị ngữ soft-delete phải chạy TRƯỚC LIMIT, nếu không campaign đã xoá vẫn chiếm chỗ trong trang.");
    }
}
