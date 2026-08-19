using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Dải mức MẶC ĐỊNH (<see cref="ScoringCriteriaBuilder.DefaultBand"/>) — thứ quyết định prompt chấm
/// của <b>100% lượt chấm trên production</b>, vì <c>rubric_levels</c> hiện có 0 dòng.
///
/// <para><b>Vì sao nhóm test này tồn tại.</b> Dải mặc định là một THƯỚC ĐO, và mọi cách nó hỏng đều
/// im lặng: mốc lệch giữa hai hàm ⇒ điểm hợp lệ bị coi là ngoài mức rồi snap/drop; mốc trùng ⇒ prompt
/// có hai dòng cùng số; thiếu 0 hoặc thiếu <c>maxScore</c> ⇒ không ai đạt được hai đầu thang; cờ quay
/// lui không quay về đúng hành vi cũ ⇒ "đã tắt" mà điểm vẫn khác. Không cái nào ném exception, không
/// cái nào làm test khác đỏ — chỉ là điểm số mang nghĩa khác.</para>
///
/// <para><b>Trạng thái:</b> <see cref="DefaultBandStyle.EveryInteger"/> là MẶC ĐỊNH (hành vi có từ
/// E9). <see cref="DefaultBandStyle.Descriptive"/> là opt-in đang chờ nghiệm thu — lý do và cách
/// nghiệm thu nằm ở doc của <see cref="ScoringCriteriaBuilder.DefaultBand"/>.</para>
/// </summary>
public class DefaultBandTests
{
    // Thang từ 1 tới 30 phủ trọn khoảng thật: 5 (bộ chuẩn B2C) · 10/15/18/20/30 (rubric riêng người
    // dùng tự đặt) · và cả 1..4 là chỗ số mốc buộc phải TỰ CO để không đẻ mốc trùng.
    public static TheoryData<int> AllMaxScores
    {
        get
        {
            var data = new TheoryData<int>();
            for (var m = 1; m <= 30; m++) data.Add(m);
            return data;
        }
    }

    // ── (1) HỢP ĐỒNG: DefaultBand ↔ ValidLevelScores ─────────────────────────────────────────

    /// <summary>
    /// 🔴 Bất biến quan trọng nhất file này. <c>Build</c> gửi <c>levels</c> sang worker Python (worker
    /// suy <c>levels_by_id</c> từ đúng mảng đó), còn <c>ValidLevelScores</c> là guard phía C# ở
    /// callback. Hai hàm lệch nhau ⇒ điểm worker gửi về bị coi là "ngoài mức" ⇒ snap/drop ⇒ MẤT ĐIỂM
    /// IM LẶNG. Khoá cho MỌI thang 1..30 và CẢ HAI kiểu dải.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMaxScores))]
    public void ValidLevelScores_KhopTungDiem_VoiDefaultBand_OMoiThang(int maxScore)
    {
        foreach (var style in new[] { DefaultBandStyle.EveryInteger, DefaultBandStyle.Descriptive })
        {
            var band = ScoringCriteriaBuilder.DefaultBand(maxScore, "vi", style).Select(l => l.Score).ToArray();
            var valid = ScoringCriteriaBuilder.ValidLevelScores([], maxScore, style).ToArray();

            Assert.Equal(band, valid);
        }
    }

    /// <summary>Ngôn ngữ chỉ đổi CHỮ, không đổi tập ĐIỂM — nếu không thì guard sẽ phụ thuộc ngôn ngữ.</summary>
    [Theory]
    [MemberData(nameof(AllMaxScores))]
    public void DefaultBand_TapDiem_KhongPhuThuocNgonNgu(int maxScore)
    {
        foreach (var style in new[] { DefaultBandStyle.EveryInteger, DefaultBandStyle.Descriptive })
            Assert.Equal(
                ScoringCriteriaBuilder.DefaultBand(maxScore, "vi", style).Select(l => l.Score),
                ScoringCriteriaBuilder.DefaultBand(maxScore, "en", style).Select(l => l.Score));
    }

    /// <summary>Tiêu chí CÓ khai mốc thì <c>ValidLevelScores</c> lấy đúng mốc khai — cờ không đụng tới.</summary>
    [Fact]
    public void ValidLevelScores_CoMocKhai_LayMocKhai_BatKeCo()
    {
        List<RubricLevel> declared =
        [
            new() { Score = 5, Descriptor = "cao" },
            new() { Score = 0, Descriptor = "thấp" },
            new() { Score = 3, Descriptor = "giữa" }
        ];

        foreach (var style in new[] { DefaultBandStyle.EveryInteger, DefaultBandStyle.Descriptive })
            Assert.Equal([0, 3, 5], ScoringCriteriaBuilder.ValidLevelScores(declared, 5, style));
    }

    // ── (2) Tính chất của dải Descriptive ────────────────────────────────────────────────────

    /// <summary>
    /// Mốc phải là SỐ NGUYÊN hợp lệ trong <c>[0, maxScore]</c>, tăng NGẶT (⇒ không trùng), và luôn
    /// gồm cả hai đầu thang. Thiếu 0 thì "không trả lời được" không có chỗ đặt; thiếu <c>maxScore</c>
    /// thì không ai đạt điểm tối đa được — cả hai đều lệch điểm mà không có triệu chứng.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMaxScores))]
    public void Descriptive_MocNguyen_TangNgat_GomCa0VaMax(int maxScore)
    {
        var scores = ScoringCriteriaBuilder
            .DefaultBand(maxScore, "vi", DefaultBandStyle.Descriptive)
            .Select(l => l.Score)
            .ToList();

        Assert.Equal(0, scores.First());
        Assert.Equal(maxScore, scores.Last());
        Assert.All(scores, s => Assert.InRange(s, 0, maxScore));
        for (var i = 1; i < scores.Count; i++)
            Assert.True(scores[i] > scores[i - 1],
                $"thang {maxScore}: mốc không tăng ngặt ({string.Join(",", scores)})");
        Assert.Equal(scores.Count, scores.Distinct().Count());
    }

    /// <summary>
    /// Số mốc CÓ TRẦN và KHÔNG phụ thuộc <c>maxScore</c> — đây là nửa còn lại của thay đổi: thang 30
    /// ở dải cũ đẻ 31 dòng prompt. Thang nhỏ (1..4) tự co xuống <c>maxScore+1</c> thay vì nhồi cho đủ
    /// 6 mốc (nhồi = mốc trùng).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMaxScores))]
    public void Descriptive_SoMoc_CoTran_VaTuCoOThangNho(int maxScore)
    {
        var count = ScoringCriteriaBuilder.DefaultBand(maxScore, "vi", DefaultBandStyle.Descriptive).Count;

        Assert.True(count <= ScoringCriteriaBuilder.MaxDefaultBandLevels,
            $"thang {maxScore}: {count} mốc, vượt trần {ScoringCriteriaBuilder.MaxDefaultBandLevels}");
        Assert.Equal(Math.Min(ScoringCriteriaBuilder.MaxDefaultBandLevels, maxScore + 1), count);
    }

    /// <summary>
    /// Descriptor phải ĐỘC LẬP THANG: cùng số mốc thì cùng bộ chữ, dù thang 5 hay thang 30. Đây là
    /// cái neo — "câu này thuộc bậc nào" trả lời được từ bản ghi, còn "17 hay 18 trên 30" thì không.
    /// </summary>
    [Fact]
    public void Descriptive_Descriptor_DocLapThang()
    {
        var thang5 = ScoringCriteriaBuilder.DefaultBand(5, "vi", DefaultBandStyle.Descriptive);
        var thang30 = ScoringCriteriaBuilder.DefaultBand(30, "vi", DefaultBandStyle.Descriptive);

        Assert.Equal(
            thang5.Select(l => l.Descriptor),
            thang30.Select(l => l.Descriptor));
        Assert.NotEqual(
            thang5.Select(l => l.Score),
            thang30.Select(l => l.Score));
    }

    /// <summary>
    /// KHÔNG được viết lại "Mức i/max" bằng chữ khác: descriptor phải nói ĐƯỢC/KHÔNG ĐƯỢC cái gì, và
    /// không chứa chính con số mốc (chứa số = lại tautology, chỉ dài hơn).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMaxScores))]
    public void Descriptive_Descriptor_KhongLapLaiConSo(int maxScore)
    {
        foreach (var lang in new[] { "vi", "en" })
            foreach (var lv in ScoringCriteriaBuilder.DefaultBand(maxScore, lang, DefaultBandStyle.Descriptive))
            {
                Assert.False(string.IsNullOrWhiteSpace(lv.Descriptor));
                Assert.DoesNotContain($"{lv.Score}/{maxScore}", lv.Descriptor);
                Assert.DoesNotContain(lv.Descriptor, c => char.IsDigit(c));
            }
    }

    /// <summary>Song ngữ theo tham số <c>language</c> như dải cũ — "en" ra chữ Anh, còn lại ra chữ Việt.</summary>
    [Fact]
    public void Descriptive_SongNgu_TheoThamSoLanguage()
    {
        var vi = ScoringCriteriaBuilder.DefaultBand(5, "vi", DefaultBandStyle.Descriptive);
        var en = ScoringCriteriaBuilder.DefaultBand(5, "en", DefaultBandStyle.Descriptive);

        Assert.StartsWith("Không đáp ứng", vi.First().Descriptor);
        Assert.StartsWith("Xuất sắc", vi.Last().Descriptor);
        Assert.StartsWith("Not met", en.First().Descriptor);
        Assert.StartsWith("Excellent", en.Last().Descriptor);
        Assert.Equal(vi.Count, en.Count);
    }

    /// <summary>
    /// Bộ mốc CỤ THỂ cho 4 thang hay gặp. Đây là ảnh chụp có chủ đích: nó biến mọi thay đổi công thức
    /// thành một diff đọc được, thay vì một dịch chuyển vài phần trăm điểm mà không ai truy ra nguồn.
    /// </summary>
    [Theory]
    [InlineData(3, new[] { 0, 1, 2, 3 })]
    [InlineData(5, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(10, new[] { 0, 2, 4, 6, 8, 10 })]
    [InlineData(30, new[] { 0, 6, 12, 18, 24, 30 })]
    public void Descriptive_BoMoc_ChoCacThangHayGap(int maxScore, int[] expected)
        => Assert.Equal(
            expected,
            ScoringCriteriaBuilder.DefaultBand(maxScore, "vi", DefaultBandStyle.Descriptive)
                .Select(l => l.Score).ToArray());

    /// <summary>
    /// Bộ mốc ngắn vẫn giữ hai đầu và lấy bậc giữa cách đều — thang 3 ⇒ 4 mốc, nhãn không được dồn
    /// hết về một phía.
    /// </summary>
    [Fact]
    public void Descriptive_ThangNho_NhanVanTraiDuTuThapToiCao()
    {
        var band = ScoringCriteriaBuilder.DefaultBand(3, "vi", DefaultBandStyle.Descriptive);

        Assert.Equal(4, band.Count);
        Assert.StartsWith("Không đáp ứng", band[0].Descriptor);
        Assert.StartsWith("Trung bình", band[1].Descriptor);
        Assert.StartsWith("Khá", band[2].Descriptor);
        Assert.StartsWith("Xuất sắc", band[3].Descriptor);
        Assert.Equal(band.Count, band.Select(l => l.Descriptor).Distinct().Count());
    }

    /// <summary>Thang méo (<c>maxScore</c> ≤ 0) không được chia cho 0 — trả đúng 1 mốc, như dải cũ.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Descriptive_ThangMeo_TraDungMotMoc(int maxScore)
    {
        var band = ScoringCriteriaBuilder.DefaultBand(maxScore, "vi", DefaultBandStyle.Descriptive);

        var lv = Assert.Single(band);
        Assert.Equal(0, lv.Score);
        Assert.Equal(band.Select(l => l.Score), ScoringCriteriaBuilder.ValidLevelScores([], maxScore, DefaultBandStyle.Descriptive));
    }

    // ── (3) Cần gạt quay lui ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cờ quay lui phải trả về hành vi cũ TỪNG BYTE: mọi số nguyên 0..maxScore, descriptor
    /// "Mức i/max" (vi) / "Level i/max" (en). "Gần giống" là vô dụng — quay lui tồn tại để đối chứng
    /// với chính số đo cũ.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMaxScores))]
    public void EveryInteger_TraVeDungHanhViCu(int maxScore)
    {
        var vi = ScoringCriteriaBuilder.DefaultBand(maxScore, "vi", DefaultBandStyle.EveryInteger);
        var en = ScoringCriteriaBuilder.DefaultBand(maxScore, "en", DefaultBandStyle.EveryInteger);

        Assert.Equal(maxScore + 1, vi.Count);
        Assert.Equal(Enumerable.Range(0, maxScore + 1), vi.Select(l => l.Score));
        Assert.All(vi, l => Assert.Equal($"Mức {l.Score}/{maxScore}", l.Descriptor));
        Assert.All(en, l => Assert.Equal($"Level {l.Score}/{maxScore}", l.Descriptor));
    }

    /// <summary>
    /// MẶC ĐỊNH của cấu hình = hành vi cũ. Đổi thước đo phải là một QUYẾT ĐỊNH sau khi đo, không phải
    /// thứ lặng lẽ đi kèm một lần deploy. Và đã đo rồi: dải mới KHÔNG cải thiện tái lập (90,7% → 92,1%
    /// cặp cùng điểm, chênh 1,4 điểm phần trăm với sai số chuẩn của hiệu ≈ 2,7). Nên test này không
    /// chỉ khoá một mặc định — nó khoá một KẾT LUẬN. Chi tiết ở `ScoringCriteriaBuilder.DefaultBand`.
    /// </summary>
    [Fact]
    public void MacDinh_LaHanhViCu_OCaCauHinhLanChuKyHam()
    {
        Assert.Equal(DefaultBandStyle.EveryInteger, new ScoringOptions().DefaultBandStyle);

        // Tham số mặc định của 2 hàm public cũng phải là hành vi cũ (call site quên truyền cờ ⇒ y như trước).
        Assert.Equal(
            ScoringCriteriaBuilder.DefaultBand(5, "vi", DefaultBandStyle.EveryInteger).Select(l => l.Score),
            ScoringCriteriaBuilder.DefaultBand(5).Select(l => l.Score));
        Assert.Equal(
            ScoringCriteriaBuilder.ValidLevelScores([], 5, DefaultBandStyle.EveryInteger),
            ScoringCriteriaBuilder.ValidLevelScores([], 5));
    }

    // ── (4) Tiêu chí CÓ khai rubric_levels: cờ không được chạm tới ───────────────────────────

    /// <summary>
    /// Mốc do người soạn khai là thước đo THẬT; cờ chỉ nói về cái sàn dựng thay khi THIẾU mốc. Hai kiểu
    /// dải phải cho ra payload GIỐNG HỆT nhau khi tiêu chí đã khai mốc (kể cả anchor).
    /// </summary>
    [Fact]
    public void CoKhaiMoc_HaiKieuDai_ChoRaPayloadGiongHet()
    {
        var crit = TestDb.Criterion(JobCategory.BE);
        crit.MaxScore = 30;   // thang lớn = chỗ hai kiểu dải khác nhau nhiều nhất, nếu cờ lọt vào
        crit.Levels =
        [
            new RubricLevel { CriterionId = crit.Id, Score = 0, Descriptor = "Không trả lời" },
            new RubricLevel
            {
                CriterionId = crit.Id, Score = 30, Descriptor = "Trả lời đầy đủ",
                ExampleAnswers = ["DI là tiêm phụ thuộc..."]
            }
        ];

        var cu = ScoringCriteriaBuilder.Build([crit], DefaultBandStyle.EveryInteger)[0];
        var moi = ScoringCriteriaBuilder.Build([crit], DefaultBandStyle.Descriptive)[0];

        Assert.Equal([0, 30], cu.Levels.Select(l => l.Score).ToArray());
        Assert.Equal(
            cu.Levels.Select(l => (l.Score, l.Descriptor)),
            moi.Levels.Select(l => (l.Score, l.Descriptor)));
        Assert.Equal(
            cu.Anchors!.Select(a => (a.Score, a.ExampleAnswer)),
            moi.Anchors!.Select(a => (a.Score, a.ExampleAnswer)));
    }

    // ── (5) Cờ có THẬT SỰ tới được message chấm không ────────────────────────────────────────

    /// <summary>
    /// Cờ vô dụng nếu đường publish không đọc nó — và đó đúng là kiểu hỏng im lặng: cấu hình ghi
    /// "Descriptive", log không nói gì, prompt vẫn là dải cũ. Test đi qua <see cref="AnswerService"/>
    /// THẬT tới tận <c>ScoringJob</c> được publish.
    /// </summary>
    [Theory]
    [InlineData(DefaultBandStyle.EveryInteger, 6, "Mức 0/5")]        // thang 5 ⇒ 0..5, descriptor = con số
    [InlineData(DefaultBandStyle.Descriptive, 6, "Không đáp ứng")]   // thang 5 ⇒ trần 6 mốc: cùng SỐ LƯỢNG, khác CHỮ
    public async Task Upload_DuongPublish_DocCoDefaultBandStyle(
        DefaultBandStyle style, int expectedCount, string expectedFirstDescriptor)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);   // maxScore 5, không khai rubric_levels
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        ScoringJob? published = null;
        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        var svc = new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object,
            Options.Create(new ScoringOptions { DefaultBandStyle = style }),
            NullLogger<AnswerService>.Instance);

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.NotNull(published);
        var sent = Assert.Single(published!.Criteria);
        Assert.Equal(expectedCount, sent.Levels.Count);
        Assert.StartsWith(expectedFirstDescriptor, sent.Levels[0].Descriptor);
        Assert.Equal(
            ScoringCriteriaBuilder.DefaultBand(crit.MaxScore, crit.Language, style)
                .Select(l => (l.Score, l.Descriptor)),
            sent.Levels.Select(l => (l.Score, l.Descriptor)));
    }
}
