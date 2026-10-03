using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Ảnh KHÔNG giải mã được (AIService 422 <c>IMAGE_UNREADABLE</c>) là lỗi của DỮ LIỆU, không phải hạ tầng.
///
/// Dev 03/10: 4 byte chữ ký JPEG + 4000 byte rác gửi vào face-enroll → AIService /face-detect 502 →
/// client đổi thành DownstreamServiceException → controller FAIL-OPEN nhận mốc rác (204, gán
/// reference_image_key, Start báo faceEnrollRequired=false) → face-check bằng người thật 502 → 0 cờ.
/// Comment trong code hứa "mốc hỏng sẽ lộ ra bằng identity_unverified" nhưng thực tế không lộ.
///
/// Khoá ở đây:
///  • enroll: ảnh rác → 400 REFERENCE_UNREADABLE (faceCount null), dọn object mới + dòng sổ, mốc cũ còn;
///  • 5xx / timeout VẪN fail-open (SEC-5) — chỉ 422 có mã mới đổi hành vi;
///  • 422 KHÔNG mã (pydantic — khoá JSON lệch hợp đồng) vẫn là hạ tầng, nếu không lệch hợp đồng = chặn mọi ứng viên;
///  • face-check: mốc rác → cờ identity_unverified; live rác → cờ no_face (đã chốt với user: gửi ảnh live rác
///    là cách né kiểm mặt, ảnh live đã vào sổ trước khi kiểm nên không cờ = trông như đang được giám sát);
///  • hợp đồng hai phía: body 422 mà AIService trả (mẫu dán NGUYÊN VĂN từ test_face_unreadable_contract.py)
///    đi qua client THẬT tới đúng nhánh controller.
/// </summary>
public class FaceUnreadableImageTests
{
    private static readonly Guid FixedSession = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // Body 422 AIService trả — giữ GIỐNG HỆT mẫu trong tests/test_face_unreadable_contract.py (Python).
    private const string Unreadable422Reference =
        "{\"detail\":{\"code\":\"IMAGE_UNREADABLE\",\"image\":\"reference\",\"message\":\"Không giải mã được ảnh 'reference' (định dạng không hợp lệ hoặc dữ liệu hỏng).\"}}";
    private const string Unreadable422Live =
        "{\"detail\":{\"code\":\"IMAGE_UNREADABLE\",\"image\":\"live\",\"message\":\"Không giải mã được ảnh 'live' (định dạng không hợp lệ hoặc dữ liệu hỏng).\"}}";
    private const string Unreadable422Image =
        "{\"detail\":{\"code\":\"IMAGE_UNREADABLE\",\"image\":\"image\",\"message\":\"Không giải mã được ảnh 'image' (định dạng không hợp lệ hoặc dữ liệu hỏng).\"}}";
    // 422 của pydantic khi body thiếu khoá — đúng hình dạng FastAPI trả (detail là MẢNG).
    private const string Pydantic422 =
        "{\"detail\":[{\"type\":\"missing\",\"loc\":[\"body\",\"imageKey\"],\"msg\":\"Field required\",\"input\":{}}]}";

    // ── hạ tầng giả ──────────────────────────────────────────────────────────────────
    private sealed class FakeFileService : IFileService
    {
        public readonly List<string> Uploaded = new();
        public readonly List<string> Deleted = new();
        public Func<string, Task>? OnDelete;
        public bool FailDelete;

        public Task<string> UploadAsync(IFormFile file, string path, CancellationToken ct = default)
        {
            Uploaded.Add(path);
            return Task.FromResult(path);
        }

        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            if (OnDelete is not null) await OnDelete(path);
            if (FailDelete) throw new InvalidOperationException($"S3 down for {path}");
            Deleted.Add(path);
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public string GetUrl(string path) => path;
    }

    /// <summary>AIService giả ở tầng HTTP: trả đúng status + body cho từng đường dẫn.</summary>
    private sealed class AiHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode Status, string Body)> Routes = new();
        public TimeSpan Delay = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            var (status, body) = Routes[request.RequestUri!.AbsolutePath];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static AiServiceFaceVerifyClient RealClient(AiHandler h, string? detectTimeoutSeconds = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "tok",
            ["AiService:FaceDetectTimeoutSeconds"] = detectTimeoutSeconds
        }).Build();
        return new AiServiceFaceVerifyClient(
            new HttpClient(h) { BaseAddress = new Uri("http://ai.local") }, config,
            NullLogger<AiServiceFaceVerifyClient>.Instance);
    }

    private static FaceVerifyController NewController(
        CampaignDbContext db, Guid candidateId, IFileService file, IAiServiceFaceVerifyClient ai)
    {
        var controller = new FaceVerifyController(db, file, ai, NullLogger<FaceVerifyController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, candidateId.ToString()) }, "Test"))
            }
        };
        return controller;
    }

    private static IFormFile JunkJpeg()
    {
        // Đúng hình dạng request tái hiện bug: chữ ký JPEG (FF D8 FF E0) + 4000 byte rác.
        var bytes = new byte[4004];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        for (var i = 4; i < bytes.Length; i++) bytes[i] = (byte)(i * 37 + 11);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "junk.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private const string OldRefKey = "campaigns/old/face-reference-0000.jpg";

    /// <summary>Campaign bật face-verify + membership giữ FixedSession; tuỳ chọn mốc cũ kèm dòng sổ.</summary>
    private static (Campaign campaign, Guid candidateId) Seed(CampaignTestDb t, string? existingRef = null)
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        c.FaceVerifyEnabled = true;
        t.Db.Campaigns.Add(c);
        var candidateId = Guid.NewGuid();
        t.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(
            c.Id, candidateId, sessionId: FixedSession, referenceImageKey: existingRef));
        if (existingRef is not null)
            t.Db.FaceImages.Add(new FaceImage
            {
                Id = Guid.NewGuid(), CampaignId = c.Id, CandidateId = candidateId, SessionId = null,
                Kind = FaceImageKind.Reference, StorageKey = existingRef, CapturedAt = DateTime.UtcNow.AddMinutes(-5)
            });
        t.Db.SaveChanges();
        return (c, candidateId);
    }

    private static string? RefKeyInDb(CampaignTestDb t)
        => t.NewContext().CampaignMemberships.AsNoTracking().Single().ReferenceImageKey;

    private static List<string> LedgerKeys(CampaignTestDb t, FaceImageKind kind)
        => t.NewContext().FaceImages.AsNoTracking().Where(x => x.Kind == kind)
            .Select(x => x.StorageKey).OrderBy(x => x).ToList();

    private static List<SessionFlag> Flags(CampaignTestDb t)
        => t.NewContext().SessionFlags.AsNoTracking().ToList();

    private static JsonElement BadBody(IActionResult r)
        => JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(r).Value);

    private static Mock<IAiServiceFaceVerifyClient> DetectThrows(Exception ex)
    {
        var m = new Mock<IAiServiceFaceVerifyClient>();
        m.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        return m;
    }

    private static Mock<IAiServiceFaceVerifyClient> VerifyThrows(Exception ex)
    {
        var m = new Mock<IAiServiceFaceVerifyClient>();
        m.Setup(x => x.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
        return m;
    }

    // ═══ Client: 422 có mã → ImageUnreadableException; mọi thứ khác → Downstream ═══════════════════
    [Fact]
    public async Task Client_Detect_422CoMa_NemImageUnreadable_Image()
    {
        var h = new AiHandler();
        h.Routes["/api/v1/face-detect"] = (HttpStatusCode.UnprocessableEntity, Unreadable422Image);
        var ex = await Assert.ThrowsAsync<ImageUnreadableException>(() => RealClient(h).DetectAsync("k"));
        Assert.Equal("image", ex.Image);
        Assert.IsNotAssignableFrom<DownstreamServiceException>(ex);
    }

    [Theory]
    [InlineData(Unreadable422Reference, "reference")]
    [InlineData(Unreadable422Live, "live")]
    public async Task Client_Verify_422CoMa_NemImageUnreadable_DungAnh(string body, string image)
    {
        var h = new AiHandler();
        h.Routes["/api/v1/face-verify"] = (HttpStatusCode.UnprocessableEntity, body);
        var ex = await Assert.ThrowsAsync<ImageUnreadableException>(() => RealClient(h).VerifyAsync("r", "l"));
        Assert.Equal(image, ex.Image);
    }

    // 422 KHÔNG có mã = pydantic từ chối body (khoá JSON lệch hợp đồng) ⇒ VẪN là hạ tầng. Coi nó là "ảnh
    // hỏng" thì một lần đổi tên khoá biến face-enroll thành cửa chặn MỌI ứng viên.
    [Theory]
    [InlineData(Pydantic422)]
    [InlineData("{\"detail\":\"x\"}")]
    [InlineData("{\"detail\":{\"code\":\"SOMETHING_ELSE\",\"image\":\"reference\"}}")]
    [InlineData("không phải json")]
    [InlineData("{}")]                                  // 422 không có khoá detail
    [InlineData("{\"error\":\"Unprocessable\"}")]      // proxy/gateway trả 422 với body khác hẳn
    public async Task Client_422KhongMa_VanLaDownstream(string body)
    {
        var h = new AiHandler();
        h.Routes["/api/v1/face-detect"] = (HttpStatusCode.UnprocessableEntity, body);
        h.Routes["/api/v1/face-verify"] = (HttpStatusCode.UnprocessableEntity, body);
        await Assert.ThrowsAsync<DownstreamServiceException>(() => RealClient(h).DetectAsync("k"));
        await Assert.ThrowsAsync<DownstreamServiceException>(() => RealClient(h).VerifyAsync("r", "l"));
    }

    // Mã IMAGE_UNREADABLE trên status KHÁC 422 không được hiểu là ảnh hỏng (chỉ 422 mang nghĩa đó).
    [Fact]
    public async Task Client_502MangBodyCoMa_VanLaDownstream()
    {
        var h = new AiHandler();
        h.Routes["/api/v1/face-detect"] = (HttpStatusCode.BadGateway, Unreadable422Image);
        await Assert.ThrowsAsync<DownstreamServiceException>(() => RealClient(h).DetectAsync("k"));
    }

    // ═══ Enroll ════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Enroll_AnhKhongDoc_400ReferenceUnreadable_DonAnhMoi_GiuMocCu()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var files = new FakeFileService();

        var result = await NewController(t.NewContext(), cand, files,
                DetectThrows(new ImageUnreadableException("image")).Object)
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        var body = BadBody(result);
        Assert.Equal("REFERENCE_UNREADABLE", body.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("faceCount").ValueKind);   // chưa đọc được ⇒ không biết, KHÔNG phải 0
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));

        var newKey = Assert.Single(files.Uploaded);
        Assert.Equal(new[] { newKey }, files.Deleted);                              // chỉ ảnh MỚI rời S3
        Assert.Equal(OldRefKey, RefKeyInDb(t));                                     // membership KHÔNG đổi
        Assert.Equal(new[] { OldRefKey }, LedgerKeys(t, FaceImageKind.Reference));  // dòng sổ mới bị dọn, cũ còn
    }

    [Fact]
    public async Task Enroll_AnhKhongDoc_ChuaCoMoc_VanChuaCoMoc()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t);
        var files = new FakeFileService();

        var result = await NewController(t.NewContext(), cand, files,
                DetectThrows(new ImageUnreadableException("image")).Object)
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.Equal("REFERENCE_UNREADABLE", BadBody(result).GetProperty("code").GetString());
        Assert.Null(RefKeyInDb(t));                       // ⇒ Start vẫn báo faceEnrollRequired = true
        Assert.Equal(files.Uploaded, files.Deleted);
        Assert.Empty(LedgerKeys(t, FaceImageKind.Reference));
    }

    // Thứ tự dọn bắt buộc S3 TRƯỚC, sổ SAU (BK25): lúc xoá S3 dòng sổ phải còn; S3 hỏng ⇒ GIỮ dòng sổ để
    // FaceImagePurger dọn, và câu trả lời vẫn là 400 (không đổi thành 500 vì lỗi dọn dẹp).
    [Fact]
    public async Task Enroll_AnhKhongDoc_XoaS3TruocSoSau()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var files = new FakeFileService();
        bool? ledgerExistedAtDelete = null;
        files.OnDelete = async key =>
            ledgerExistedAtDelete = await t.NewContext().FaceImages.AsNoTracking().AnyAsync(x => x.StorageKey == key);

        var result = await NewController(t.NewContext(), cand, files,
                DetectThrows(new ImageUnreadableException("image")).Object)
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.Equal("REFERENCE_UNREADABLE", BadBody(result).GetProperty("code").GetString());
        Assert.True(ledgerExistedAtDelete);                                          // sổ còn lúc xoá S3 ⇒ S3 trước
        Assert.Equal(new[] { OldRefKey }, LedgerKeys(t, FaceImageKind.Reference));  // rồi mới gỡ dòng sổ
    }

    [Fact]
    public async Task Enroll_AnhKhongDoc_S3Loi_GiuDongSo_Van400()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t);
        var files = new FakeFileService { FailDelete = true };

        var result = await NewController(t.NewContext(), cand, files,
                DetectThrows(new ImageUnreadableException("image")).Object)
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.Equal("REFERENCE_UNREADABLE", BadBody(result).GetProperty("code").GetString());
        Assert.Equal(files.Uploaded, LedgerKeys(t, FaceImageKind.Reference));   // object còn ⇒ dấu vết còn
        Assert.Null(RefKeyInDb(t));
    }

    // Hợp đồng hai phía, chiều enroll: body 422 THẬT của AIService đi qua client THẬT ⇒ 400, KHÔNG 204.
    [Fact]
    public async Task Enroll_DauCuoi_422ThatQuaClientThat_400_KhongFailOpen()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var files = new FakeFileService();
        var h = new AiHandler();
        h.Routes["/api/v1/face-detect"] = (HttpStatusCode.UnprocessableEntity, Unreadable422Image);

        var result = await NewController(t.NewContext(), cand, files, RealClient(h))
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.Equal("REFERENCE_UNREADABLE", BadBody(result).GetProperty("code").GetString());
        Assert.Equal(OldRefKey, RefKeyInDb(t));
    }

    // 5xx / 422-pydantic / hết giờ ⇒ VẪN fail-open (SEC-5): mốc mới được nhận, mốc cũ được dọn.
    [Theory]
    [InlineData(HttpStatusCode.BadGateway, Unreadable422Image)]
    [InlineData(HttpStatusCode.InternalServerError, "{\"detail\":\"boom\"}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, Pydantic422)]
    public async Task Enroll_DauCuoi_LoiHaTang_VanFailOpen_204(HttpStatusCode status, string body)
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var files = new FakeFileService();
        var h = new AiHandler();
        h.Routes["/api/v1/face-detect"] = (status, body);

        var result = await NewController(t.NewContext(), cand, files, RealClient(h))
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.IsType<NoContentResult>(result);
        var newKey = Assert.Single(files.Uploaded);
        Assert.Equal(newKey, RefKeyInDb(t));
        Assert.Equal(new[] { OldRefKey }, files.Deleted);
    }

    [Fact]
    public async Task Enroll_DauCuoi_HetGio_VanFailOpen_204()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t);
        var files = new FakeFileService();
        var h = new AiHandler { Delay = TimeSpan.FromSeconds(10) };
        h.Routes["/api/v1/face-detect"] = (HttpStatusCode.OK, "{\"faceCount\":1,\"signals\":[]}");

        var result = await NewController(t.NewContext(), cand, files, RealClient(h, "1"))
            .Enroll(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(Assert.Single(files.Uploaded), RefKeyInDb(t));
    }

    // ═══ Face-check ════════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Check_MocKhongDoc_CoIdentityUnverified_200()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);

        var result = await NewController(t.NewContext(), cand, new FakeFileService(),
                VerifyThrows(new ImageUnreadableException(ImageUnreadableException.Reference)).Object)
            .Check(camp.Id, FixedSession, JunkJpeg(), default);

        var body = Assert.IsType<FaceCheckResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.Match);
        Assert.Equal(new[] { "identity_unverified" }, body.Signals);
        var flag = Assert.Single(Flags(t));
        Assert.Equal("identity_unverified", flag.SignalType);
        Assert.Equal(FaceVerifyController.ReferenceUnreadableNote, flag.Note);
        Assert.Contains("Ảnh mốc không đọc được", flag.Note);
        Assert.Equal(FixedSession, flag.SessionId);
    }

    [Fact]
    public async Task Check_LiveKhongDoc_CoNoFace_200()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var files = new FakeFileService();

        var result = await NewController(t.NewContext(), cand, files,
                VerifyThrows(new ImageUnreadableException(ImageUnreadableException.Live)).Object)
            .Check(camp.Id, FixedSession, JunkJpeg(), default);

        var body = Assert.IsType<FaceCheckResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new[] { "no_face" }, body.Signals);
        Assert.Equal(0, body.FaceCount);
        var flag = Assert.Single(Flags(t));
        Assert.Equal("no_face", flag.SignalType);
        Assert.Equal(FaceVerifyController.LiveUnreadableNote, flag.Note);
        Assert.StartsWith("Ảnh kiểm mặt không đọc được", flag.Note);
        // ảnh live rác vẫn nằm trong sổ (BK25) — chính vì thế mà không cờ = trông như vẫn được giám sát
        Assert.Equal(files.Uploaded, LedgerKeys(t, FaceImageKind.Live));
    }

    [Fact]
    public async Task Check_TenAnhLa_502_KhongDoanCo()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);

        var result = await NewController(t.NewContext(), cand, new FakeFileService(),
                VerifyThrows(new ImageUnreadableException("thumbnail")).Object)
            .Check(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(Flags(t));
    }

    // Hợp đồng hai phía, chiều face-check: body 422 THẬT qua client THẬT ⇒ đúng cờ, không 502.
    [Theory]
    [InlineData(Unreadable422Reference, "identity_unverified")]
    [InlineData(Unreadable422Live, "no_face")]
    public async Task Check_DauCuoi_422ThatQuaClientThat_DungCo(string aiBody, string signal)
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var h = new AiHandler();
        h.Routes["/api/v1/face-verify"] = (HttpStatusCode.UnprocessableEntity, aiBody);

        var result = await NewController(t.NewContext(), cand, new FakeFileService(), RealClient(h))
            .Check(camp.Id, FixedSession, JunkJpeg(), default);

        var body = Assert.IsType<FaceCheckResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new[] { signal }, body.Signals);
        Assert.Equal(signal, Assert.Single(Flags(t)).SignalType);
    }

    // 502 thật ở face-check vẫn 502 (lỗi hạ tầng không bịa cờ cho ứng viên).
    [Fact]
    public async Task Check_DauCuoi_502_Van502_KhongCo()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, OldRefKey);
        var h = new AiHandler();
        h.Routes["/api/v1/face-verify"] = (HttpStatusCode.BadGateway, "{\"detail\":\"Lỗi đối chiếu khuôn mặt: model down\"}");

        var result = await NewController(t.NewContext(), cand, new FakeFileService(), RealClient(h))
            .Check(camp.Id, FixedSession, JunkJpeg(), default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(Flags(t));
    }
}
