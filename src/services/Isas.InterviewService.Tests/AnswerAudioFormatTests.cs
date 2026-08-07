using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BK27 — định dạng audio phải theo FILE THẬT, không phải hằng số `webm`.
///
/// <para>Trước bản vá: mọi bản ghi âm lưu đuôi `.webm` và phát lại với `audio/webm`. iPhone thu ra m4a nên
/// vừa lưu sai đuôi (AIService suy MIME từ đuôi khi gửi file gốc lên nhà cung cấp chép lời) vừa phát lại
/// không được. Bộ test này khoá cả ba tầng: nhận dạng, lưu, phát lại.</para>
/// </summary>
public class AnswerAudioFormatTests
{
    // ---- mẫu byte đầu của từng định dạng (đủ 12 byte để phân biệt WAV) ----
    private static byte[] WebmBytes() => [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0];
    private static byte[] OggBytes() => "OggS"u8.ToArray().Concat(new byte[8]).ToArray();
    private static byte[] FlacBytes() => "fLaC"u8.ToArray().Concat(new byte[8]).ToArray();
    private static byte[] Mp3Bytes() => "ID3"u8.ToArray().Concat(new byte[9]).ToArray();
    private static byte[] M4aBytes() => new byte[4].Concat("ftypM4A "u8.ToArray()).ToArray();
    private static byte[] WavBytes() => "RIFF"u8.ToArray().Concat(new byte[4]).Concat("WAVE"u8.ToArray()).ToArray();

    /// <summary>Byte KHÔNG khớp chữ ký nào — buộc quá trình nhận dạng rơi xuống Content-Type / tên file.</summary>
    private static byte[] UnknownBytes() => [0x99, 0x98, 0x97, 0x96, 0, 0, 0, 0, 0, 0, 0, 0];

    // ================= Tầng 1: lưu đúng đuôi (qua AnswerService thật) =================

    private static AnswerService BuildService(TestDb t, out Mock<IStorageService> storage)
    {
        storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, new Mock<IScoringJobPublisher>().Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(1, 1m, minReasoningLen: 0),
            NullLogger<AnswerService>.Instance);
    }

    // Ca non-webm là phần QUAN TRỌNG: nếu chỉ có "audio/webm" thì trả code về `ext: "webm"` cứng vẫn xanh.
    [Theory]
    [InlineData("audio/webm", "webm")]
    [InlineData("audio/mp4", "m4a")]
    [InlineData("audio/mpeg", "mp3")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/ogg", "ogg")]
    [InlineData("audio/flac", "flac")]
    [InlineData("video/mp4", "mp4")]
    public async Task Upload_LuuDungDuoiTheoDinhDang(string mime, string expectedExt)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = BuildService(t, out var storage);
        using var audio = new MemoryStream([1, 2, 3]);

        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, mime, 30);

        storage.Verify(s => s.UploadAsync(
            It.IsAny<Stream>(), "answer-audio", candidate, It.IsAny<Guid>(),
            expectedExt, mime, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ================= Tầng 2: cổng nhận dạng ở controller =================

    private static AnswersController BuildController(
        Mock<IAnswerService> service, Guid candidateId, bool strictGate = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token",
            ["Audio:StrictFormatGate"] = strictGate ? "true" : "false",
        }).Build();

        return new AnswersController(service.Object, config, NullLogger<AnswersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, candidateId.ToString())], "Test"))
                }
            }
        };
    }

    private static Mock<IFormFile> FormFile(byte[] content, string? contentType, string fileName = "answer.webm")
    {
        var f = new Mock<IFormFile>();
        f.Setup(x => x.Length).Returns(content.Length);
        f.Setup(x => x.ContentType).Returns(contentType!);
        f.Setup(x => x.FileName).Returns(fileName);
        // Trả stream MỚI mỗi lần gọi — giống ASP.NET khi thân request đã được buffer. Nhờ vậy lượt đọc
        // dò chữ ký không tiêu mất dữ liệu của lượt upload thật.
        f.Setup(x => x.OpenReadStream()).Returns(() => new MemoryStream(content));
        return f;
    }

    private static Mock<IAnswerService> ServiceMock()
    {
        var svc = new Mock<IAnswerService>();
        svc.Setup(s => s.UploadAnswerAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadAnswerResult(Guid.NewGuid(), Guid.NewGuid(), AnswerStatus.Uploaded.ToString()));
        return svc;
    }

    private static void VerifyMime(Mock<IAnswerService> svc, string expected) =>
        svc.Verify(s => s.UploadAnswerAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Stream>(), expected, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

    [Fact]
    public async Task Upload_DinhDangLa_400_VaKhongGoiService()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid());
        var file = FormFile(UnknownBytes(), "application/zip", "answer.bin");

        var result = await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default);

        Assert.IsType<BadRequestObjectResult>(result);
        // Không có vế này thì một cái 400 vì lý do khác cũng làm test xanh.
        svc.Verify(s => s.UploadAnswerAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Chrome gửi kèm tham số codecs. Đây là đường sống của web hiện tại.
    [Fact]
    public async Task Upload_Chrome_WebmKemCodecs_Qua()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid());
        var file = FormFile(WebmBytes(), "audio/webm;codecs=opus");

        Assert.IsType<OkObjectResult>(await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default));
        VerifyMime(svc, "audio/webm");
    }

    // Firefox gửi "audio/ogg; codecs=opus". Byte để không-nhận-ra nhằm ép đi qua đúng nấc Content-Type.
    // (Khoảng trắng ở đây nằm SAU dấu chấm phẩy nên Split(';')[0] vốn đã sạch — ca cần Trim() là test kế bên.)
    [Fact]
    public async Task Upload_Firefox_ContentTypeKemCodecs_Qua()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid());
        var file = FormFile(UnknownBytes(), "audio/ogg; codecs=opus", "answer.bin");

        Assert.IsType<OkObjectResult>(await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default));
        VerifyMime(svc, "audio/ogg");
    }

    // Client bọc khoảng trắng quanh chính media-type. Đây mới là ca Trim() thật sự đỡ.
    [Theory]
    [InlineData(" audio/mp4 ")]
    [InlineData("  audio/mp4")]
    [InlineData("audio/mp4  ; codecs=mp4a.40.2")]
    public void ContentTypeCoKhoangTrangBaoQuanh_VanNhanRa(string contentType) =>
        Assert.Equal("m4a", AudioFormats.ExtFor(contentType));

    [Fact]
    public async Task Upload_ContentTypeChuHoa_Qua()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid());
        var file = FormFile(UnknownBytes(), "AUDIO/MP4", "answer.bin");

        Assert.IsType<OkObjectResult>(await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default));
        VerifyMime(svc, "audio/mp4");
    }

    // Safari: blob.type là audio/mp4 nhưng FE hardcode tên file answer.webm. Nội dung phải thắng cả hai.
    [Fact]
    public async Task Upload_NoiDungThangContentTypeSai()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid());
        var file = FormFile(M4aBytes(), "audio/webm", "answer.webm");

        Assert.IsType<OkObjectResult>(await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default));
        VerifyMime(svc, "audio/mp4");
    }

    // Client mobile không set Content-Type (Flutter/OkHttp cấu hình mặc định) → còn tên file để bấu víu.
    [Fact]
    public async Task Upload_ThieuContentType_SuyTuTenFile()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid());
        var file = FormFile(UnknownBytes(), null, "answer.m4a");

        Assert.IsType<OkObjectResult>(await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default));
        VerifyMime(svc, "audio/mp4");
    }

    // Kill-switch: tắt cổng → hành vi y như trước bản vá (không 400, chuyển tiếp Content-Type thô).
    [Fact]
    public async Task Upload_TatCong_KhongTuChoi_GiuHanhViCu()
    {
        var svc = ServiceMock();
        var controller = BuildController(svc, Guid.NewGuid(), strictGate: false);
        var file = FormFile(UnknownBytes(), "application/zip", "answer.bin");

        Assert.IsType<OkObjectResult>(await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, 30, default));
        VerifyMime(svc, "application/zip");
        // Và đuôi rơi về mặc định cũ, không phải chuỗi do client đặt.
        Assert.Equal("webm", AudioFormats.ExtFor("application/zip"));
    }

    // ================= Tầng 3: phát lại đúng MIME (qua PracticeService thật) =================

    [Theory]
    [InlineData("answer-audio/x/y.m4a", "audio/mp4")]
    [InlineData("answer-audio/x/y.webm", "audio/webm")]
    [InlineData("answer-audio/x/y.mp3", "audio/mpeg")]
    [InlineData("answer-audio/x/y.wav", "audio/wav")]
    [InlineData("answer-audio/x/y.ogg", "audio/ogg")]
    // Dữ liệu cũ / đuôi lạ: nói "không biết" thay vì khẳng định sai một định dạng cụ thể.
    [InlineData("answer-audio/x/y.bin", "application/octet-stream")]
    [InlineData("answer-audio/x/y", "application/octet-stream")]
    public async Task PhatLai_MimeTheoDuoiCuaKey(string objectKey, string expectedMime)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var question = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Uploaded, DateTime.UtcNow, null);
        answer.AudioObjectKey = objectKey;
        t.Db.AddRange(session, question, answer);
        await t.Db.SaveChangesAsync();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.DownloadAsync(objectKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));

        var svc = new PracticeService(
            t.Db, storage.Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

        var result = await svc.GetAnswerAudioAsync(candidate, session.Id, answer.Id);

        Assert.NotNull(result);
        Assert.Equal(expectedMime, result!.ContentType);
    }

    // ================= Bảng ánh xạ =================

    [Theory]
    [InlineData("audio/x-m4a", "m4a")]
    [InlineData("audio/m4a", "m4a")]
    [InlineData("audio/mp4a-latm", "m4a")]   // hằng MIME của Android MediaFormat
    [InlineData("audio/mp3", "mp3")]
    [InlineData("audio/x-wav", "wav")]
    [InlineData("audio/wave", "wav")]
    [InlineData("audio/vnd.wave", "wav")]
    [InlineData("audio/x-flac", "flac")]
    [InlineData("application/ogg", "ogg")]
    [InlineData("video/webm", "webm")]
    public void Alias_QuyVeDinhDangChuan(string alias, string expectedExt) =>
        Assert.Equal(expectedExt, AudioFormats.ExtFor(alias));

    // Cố ý KHÔNG nhận: không nằm trong ORIGINAL_EXTENSIONS của AIService. Nhận vào là nói dối về nội dung file.
    [Theory]
    [InlineData("audio/aac")]
    [InlineData("audio/3gpp")]
    [InlineData("audio/amr")]
    public void DinhDangNgoaiHopDong_KhongDuocNhan(string mime)
    {
        var ok = AudioFormats.TryResolve(UnknownBytes(), mime, "answer.bin", out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void NhanDang_BaoDungNguonDaQuyetDinh()
    {
        Assert.True(AudioFormats.TryResolve(WavBytes(), null, null, out _, out _, out var fromBytes));
        Assert.Equal(AudioFormatSource.MagicBytes, fromBytes);

        Assert.True(AudioFormats.TryResolve(UnknownBytes(), "audio/flac", null, out _, out _, out var fromHeader));
        Assert.Equal(AudioFormatSource.ContentType, fromHeader);

        Assert.True(AudioFormats.TryResolve(UnknownBytes(), null, "a.mp3", out _, out _, out var fromName));
        Assert.Equal(AudioFormatSource.FileName, fromName);
    }

    [Fact]
    public void ChuKyNhiPhan_NhanDungTungDinhDang()
    {
        Assert.Equal("webm", Sniff(WebmBytes()));
        Assert.Equal("ogg", Sniff(OggBytes()));
        Assert.Equal("flac", Sniff(FlacBytes()));
        Assert.Equal("mp3", Sniff(Mp3Bytes()));
        Assert.Equal("m4a", Sniff(M4aBytes()));
        Assert.Equal("wav", Sniff(WavBytes()));
        // RIFF là vỏ chung của AVI/WebP — thiếu "WAVE" thì KHÔNG được nhận là audio.
        var riffKhongWave = "RIFF"u8.ToArray().Concat(new byte[4]).Concat("AVI "u8.ToArray()).ToArray();
        Assert.False(AudioFormats.TryResolve(riffKhongWave, null, null, out _, out _, out _));
    }

    private static string Sniff(byte[] head)
    {
        Assert.True(AudioFormats.TryResolve(head, null, null, out _, out var ext, out var source));
        Assert.Equal(AudioFormatSource.MagicBytes, source);
        return ext;
    }
}
