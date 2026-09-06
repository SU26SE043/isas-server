using System.Data.Common;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP2-BE1 (nửa NGUỒN) — bộ tiêu chí gửi làm bối cảnh phải đến từ một TRUY VẤN THẬT xuống DB,
/// không phải từ navigation <c>campaign.Criteria</c>.
///
/// <para>🔴 <b>Khe nguy hiểm nhất của cả tính năng.</b> Truy vấn nạp campaign ở đường sinh câu hỏi
/// chỉ có <c>.Include(c =&gt; c.Questions)</c>. Nếu ai đó "đơn giản hoá" phần này thành
/// <c>campaign.Criteria.Select(...)</c> thì production gửi đi một <b>danh sách RỖNG</b>: không
/// exception, không log, prompt vẫn đi bình thường, HR vẫn nhận câu hỏi — tính năng no-op hoàn
/// toàn trong khi mọi thứ trông như đang chạy. Repo đã dính đúng lỗi này một lần (thiếu
/// <c>.Include(Criteria)</c> ⇒ màn danh sách campaign hiện "0 tiêu chí", PR #42).</para>
///
/// <para>🔴 <b>Và đây là bẫy làm chính bộ test này vô nghĩa nếu viết ẩu:</b> seed dữ liệu qua CÙNG
/// một <c>DbContext</c> rồi gọi service ⇒ relationship-fixup của change-tracker đã giữ sẵn entity
/// trong bộ nhớ ⇒ <c>campaign.Criteria</c> CÓ dữ liệu ⇒ mutation ở trên <b>VẪN XANH</b> trong khi
/// production rỗng. Mọi test ở đây seed qua một context RIÊNG (đã dispose) rồi mới gọi service qua
/// context khác — <see cref="Seed"/> — và
/// <see cref="Doc_truy_van_that_xuong_bang_campaign_criteria"/> chốt hạ bằng cách soi SQL, phép đo
/// duy nhất hoàn toàn miễn nhiễm với trạng thái change-tracker.</para>
/// </summary>
public class CampaignCriteriaContextSourceCmp2Tests
{
    // ───────────────────────────── hạ tầng ─────────────────────────────

    /// <summary>Ghi lại ĐÚNG thứ đi vào lời gọi AI — không chỉ nuốt tham số rồi ủy quyền.</summary>
    private sealed class RecordingGenerator : IQuestionGenerator
    {
        public int Calls { get; private set; }
        /// <summary><c>null</c> = chưa lượt nào gọi (khác <c>[]</c> = có gọi, không tiêu chí nào).</summary>
        public IReadOnlyList<QuestionCriterionContext>? LastCriteriaContext { get; private set; }

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new List<string> { "Q1" });
        }

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority, CancellationToken ct)
            => GenerateAsync(jobCategory, jdText, count, ct);

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority,
            IReadOnlyList<QuestionCriterionContext> criteriaContext, CancellationToken ct)
        {
            LastCriteriaContext = criteriaContext;
            return GenerateAsync(jobCategory, jdText, count, seniority, ct);
        }
    }

    private static CampaignSvc NewService(CampaignDbContext db, IQuestionGenerator gen) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: null, invitationOptions: null, questionGenerator: gen);

    private static CampaignCriterion Criterion(
        Guid campaignId, int order, string name, string? description = null)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = order, Name = name,
            Description = description, Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Seed campaign + tiêu chí qua một <c>DbContext</c> RIÊNG rồi DISPOSE nó.
    ///
    /// <para>⚠ Bước dispose là điều kiện đúng đắn của cả file, không phải thói quen dọn dẹp: service
    /// sẽ chạy trên một context KHÁC, nên navigation <c>Criteria</c> của nó chắc chắn rỗng — đúng như
    /// production. Seed chung context là tự bịt mắt mình (xem docblock lớp).</para>
    /// </summary>
    private static Campaign Seed(
        CampaignTestDb tdb, Guid org, params CampaignCriterion[] criteria)
    {
        var campaign = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        campaign.JDText = "Tuyển Backend .NET: EF Core, PostgreSQL, RabbitMQ.";
        campaign.Domain = "BE";
        using var db = tdb.NewContext();
        db.Campaigns.Add(campaign);
        foreach (var c in criteria)
        {
            c.CampaignId = campaign.Id;
            db.CampaignCriteria.Add(c);
        }
        db.SaveChanges();
        return campaign;
    }

    // ───────────────── (1) Tiêu chí THẬT SỰ tới được lời gọi AI ─────────────────

    /// <summary>
    /// 🔒 Bất biến trung tâm: tiêu chí đã lưu trong DB phải có mặt đủ trong lời gọi sinh câu hỏi.
    ///
    /// <para>Đây là test mà mutation "đổi truy vấn riêng thành <c>campaign.Criteria.Select(...)</c>"
    /// phải làm ĐỎ — navigation không được Include nên nó rỗng.</para>
    /// </summary>
    [Fact]
    public async Task Tieu_chi_cua_chien_dich_di_toi_luot_sinh_cau_hoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org,
            Criterion(Guid.Empty, 0, "Chiều sâu kỹ thuật", "Hiểu sâu cơ chế"),
            Criterion(Guid.Empty, 1, "Thiết kế hệ thống", "Phân rã bài toán"),
            Criterion(Guid.Empty, 2, "Giải quyết vấn đề"));
        var gen = new RecordingGenerator();

        await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        Assert.Equal(1, gen.Calls);
        Assert.NotNull(gen.LastCriteriaContext);
        Assert.Equal(3, gen.LastCriteriaContext!.Count);
        Assert.Equal(
            new[] { "Chiều sâu kỹ thuật", "Thiết kế hệ thống", "Giải quyết vấn đề" },
            gen.LastCriteriaContext.Select(c => c.Name));
        // Mô tả đi kèm — đây mới là chỗ nói rõ tiêu chí đo cái gì, thiếu nó thì bối cảnh chỉ còn
        // một danh sách tên trơ.
        Assert.Equal("Hiểu sâu cơ chế", gen.LastCriteriaContext[0].Description);
        Assert.Null(gen.LastCriteriaContext[2].Description);
    }

    /// <summary>
    /// 🔒 Phép đo MIỄN NHIỄM change-tracker: chứng minh dữ liệu đến từ một câu SQL thật chạm bảng
    /// <c>campaign_criteria</c>, chứ không phải từ entity đã nằm sẵn trong bộ nhớ.
    ///
    /// <para>Vì sao cần cả test này khi test trên đã đủ làm mutation đỏ: test trên đúng <b>nhờ</b>
    /// helper <see cref="Seed"/> dùng context riêng. Ai đó refactor helper cho "gọn" (dùng lại
    /// <c>tdb.Db</c>) sẽ làm nó xanh trở lại mà không ai thấy. Test này không có cửa đó — không có
    /// câu SQL thì không có gì để đọc, bất kể context nào đang giữ gì.</para>
    ///
    /// <para>Kèm <b>đối chứng dương</b> (<c>Assert.NotEmpty(spy.Commands)</c>): một phép đo "không
    /// thấy gì" cũng đúng khi interceptor chưa hề được đấu dây — đồng hồ chết vẫn đúng hai lần một
    /// ngày.</para>
    /// </summary>
    [Fact]
    public async Task Doc_truy_van_that_xuong_bang_campaign_criteria()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org, Criterion(Guid.Empty, 0, "Chiều sâu kỹ thuật"));
        var spy = new SqlSpy();
        var gen = new RecordingGenerator();

        await NewService(tdb.NewContext(spy), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        // Đối chứng dương: interceptor thật sự đã được đấu dây và có bắt được lệnh.
        Assert.NotEmpty(spy.Commands);
        Assert.Contains(spy.Commands, sql => sql.Contains("campaign_criteria"));
        Assert.Single(gen.LastCriteriaContext!);
    }

    /// <summary>
    /// Thứ tự theo <c>order_no</c> (thứ tự HR sắp), KHÔNG theo thứ tự DB trả về.
    ///
    /// <para>Seed CỐ Ý chèn ngược thứ tự: nếu bỏ <c>OrderBy</c> thì prompt phụ thuộc thứ tự vật lý
    /// của bảng — cùng một chiến dịch sinh ra hai chuỗi prompt khác nhau ở hai lần bấm, và mọi phép
    /// so sánh "đổi prompt hay không" sau này mất chỗ đứng.</para>
    /// </summary>
    [Fact]
    public async Task Sap_theo_order_no_chu_khong_theo_thu_tu_chen()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org,
            Criterion(Guid.Empty, 2, "Ba"),
            Criterion(Guid.Empty, 0, "Một"),
            Criterion(Guid.Empty, 1, "Hai"));
        var gen = new RecordingGenerator();

        await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        Assert.Equal(new[] { "Một", "Hai", "Ba" },
            gen.LastCriteriaContext!.Select(c => c.Name));
    }

    /// <summary>
    /// 🔒 Câu SQL phải mang <c>ORDER BY</c> TƯỜNG MINH — không được dựa vào planner.
    ///
    /// <para>🔴 <b>Vì sao phải soi SQL thay vì soi kết quả:</b> bảng có UNIQUE index
    /// <c>(campaign_id, order_no)</c> (<c>CampaignDbContext.cs:252</c>). Planner SQLite dùng đúng
    /// index đó để thoả <c>WHERE campaign_id = …</c>, nên nó trả về rows <b>đã sẵn thứ tự
    /// order_no</b> — kể cả khi câu truy vấn KHÔNG có <c>ORDER BY</c>. Đã đo bằng mutation: bỏ
    /// <c>.OrderBy(c =&gt; c.OrderNo)</c> thì <see cref="Sap_theo_order_no_chu_khong_theo_thu_tu_chen"/>
    /// <b>vẫn XANH</b>. Test hành vi ở đây không phân biệt được, và đó là giới hạn của nền test chứ
    /// không phải của ý định.</para>
    ///
    /// <para>Nhưng thứ tự "đúng nhờ planner" là đúng do TÌNH CỜ: Postgres được tự do seq-scan (bảng
    /// tiêu chí chỉ vài dòng nên nó thường làm đúng thế), và lúc đó thứ tự là bất kỳ. Prompt hết tất
    /// định ⇒ cùng một chiến dịch sinh hai chuỗi prompt khác nhau ở hai lần bấm. Cùng lớp với bug
    /// đã biết của repo: <c>ORDER BY score DESC</c> thiếu <c>COALESCE</c> — SQLite xếp NULL cuối,
    /// Postgres xếp NULL đầu, và không test hành vi nào trên SQLite bắt được.</para>
    ///
    /// <para>⇒ Bất biến khoá ở đây là <b>câu truy vấn có tự sắp hay không</b>, đọc thẳng
    /// <c>CommandText</c>. Tiền lệ DB27 (verify hợp đồng SQL bằng chính SQL sinh ra).</para>
    /// </summary>
    [Fact]
    public async Task Truy_van_tieu_chi_co_ORDER_BY_tuong_minh()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org,
            Criterion(Guid.Empty, 0, "Một"), Criterion(Guid.Empty, 1, "Hai"));
        var spy = new SqlSpy();

        await NewService(tdb.NewContext(spy), new RecordingGenerator())
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        var criteriaSql = spy.Commands.Where(s => s.Contains("campaign_criteria")).ToArray();
        // Đối chứng dương: có bắt được câu nào không (một phép đo "không thấy gì" cũng đúng khi
        // interceptor chưa hề được đấu dây).
        Assert.NotEmpty(criteriaSql);
        Assert.All(criteriaSql, sql =>
            Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(criteriaSql, sql => sql.Contains("order_no"));
    }

    /// <summary>
    /// 🔒 Chỉ tiêu chí CỦA CHÍNH chiến dịch này — tiêu chí chiến dịch khác không được lọt vào.
    ///
    /// <para>Bỏ vế <c>WHERE campaign_id = …</c> là rò thước đo của tổ chức khác vào prompt của
    /// chiến dịch này, và nó sẽ không bao giờ có triệu chứng nào ngoài "câu hỏi hơi lạ".</para>
    /// </summary>
    [Fact]
    public async Task Khong_lay_nham_tieu_chi_cua_chien_dich_khac()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var mine = Seed(tdb, org, Criterion(Guid.Empty, 0, "Của tôi"));
        Seed(tdb, org, Criterion(Guid.Empty, 0, "Của chiến dịch khác"));
        var gen = new RecordingGenerator();

        await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, mine.Id, count: null, default);

        Assert.Equal(new[] { "Của tôi" }, gen.LastCriteriaContext!.Select(c => c.Name));
    }

    // ───────────────── (2) Chưa khai tiêu chí = trạng thái HỢP LỆ ─────────────────

    /// <summary>
    /// Chiến dịch Draft chưa khai tiêu chí ⇒ danh sách RỖNG, vẫn sinh câu hỏi bình thường.
    ///
    /// <para>Đây KHÔNG phải trạng thái lỗi: tiêu chí có thể sinh lúc publish (C8), HR gõ tay qua
    /// <c>PUT /campaign</c> (C12), hay chép từ bộ chuẩn (CAMP-20) — cả ba đều có thể xảy ra SAU lúc
    /// HR bấm sinh câu hỏi. Ném lỗi ở đây là chặn một luồng bình thường.</para>
    ///
    /// <para>Khoá <c>Empty</c> chứ không chỉ "không ném": rỗng là thứ client dịch thành
    /// <c>criteriaContext = null</c> ⇒ prompt AIService giữ nguyên xi.</para>
    /// </summary>
    [Fact]
    public async Task Chua_khai_tieu_chi_van_sinh_duoc_cau_hoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org);
        var gen = new RecordingGenerator();

        var res = await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        Assert.NotEmpty(res.Questions);
        Assert.NotNull(gen.LastCriteriaContext);
        Assert.Empty(gen.LastCriteriaContext!);
    }

    /// <summary>
    /// 🔒 Đi qua OVERLOAD MỚI, không rơi về overload cũ.
    ///
    /// <para><c>LastCriteriaContext</c> khởi tạo <c>null</c> và CHỈ overload 6 tham số gán nó. Nếu
    /// caller trong <c>CampaignService</c> gọi nhầm overload 5 tham số thì mọi test trên vẫn có thể
    /// xanh ở phần "sinh được câu hỏi", nhưng giá trị ở đây sẽ là <c>null</c> — tức bối cảnh chưa
    /// bao giờ rời khỏi service. Phân biệt <c>null</c> (không gọi) với <c>[]</c> (gọi, rỗng) chính
    /// là lý do property này nullable.</para>
    /// </summary>
    [Fact]
    public async Task Caller_goi_overload_moi_chu_khong_roi_ve_overload_cu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org);
        var gen = new RecordingGenerator();

        await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        Assert.NotNull(gen.LastCriteriaContext);
    }

    // ───────────────── (3) Không hồi quy đường sinh câu hỏi ─────────────────

    /// <summary>
    /// Thêm bước đọc tiêu chí KHÔNG được đụng vào việc ghi câu hỏi.
    ///
    /// <para>Truy vấn bối cảnh dùng <c>AsNoTracking</c> + projection nên không kéo entity nào vào
    /// change-tracker; nhưng "không nên" và "không" là hai chuyện khác nhau, và đoạn ngay dưới nó
    /// còn <c>RemoveRange</c>/<c>AddRange</c> trên graph đang được theo dõi.</para>
    /// </summary>
    [Fact]
    public async Task Van_luu_cau_hoi_binh_thuong_khi_co_tieu_chi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = Seed(tdb, org, Criterion(Guid.Empty, 0, "Chiều sâu kỹ thuật"));

        await NewService(tdb.NewContext(), new RecordingGenerator())
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(QuestionSource.AiGenerated, rows[0].Source);
        // Và tiêu chí KHÔNG bị đường sinh câu hỏi đụng vào (nó chỉ đọc).
        Assert.Single(await check.CampaignCriteria.Where(c => c.CampaignId == campaign.Id).ToListAsync());
    }

    // ───────────────────────────── SqlSpy ─────────────────────────────

    private sealed class SqlSpy : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToArray(); } }

        private void Record(DbCommand command)
        {
            lock (_commands) _commands.Add(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
