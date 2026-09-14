using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Isas.CampaignService.DTOs;
using Isas.Shared.Json;

namespace Isas.CampaignService.Tests;

/// <summary>
/// UX3-B3 — LƯỚI CHẶN LỆCH HỢP ĐỒNG cho <see cref="CampaignResponse"/> (GET /campaign/{id}) và
/// <see cref="CampaignListItemResponse"/> (GET /campaign).
///
/// <para>Đợt rà UX3 tìm được &gt;30 trường frontend đọc mà backend không gửi (vd danh sách campaign:
/// FE đọc <c>applicants</c>/<c>applicantCount</c> ⇒ luôn 0, trong khi backend gửi
/// <c>cvCount</c>/<c>invitedCount</c>/<c>completedCount</c>). KHÔNG lỗi nào nổ. Test này biến việc
/// ĐỔI TÊN một trường thành một test ĐỎ ngay tại backend.</para>
///
/// <para>Chỉ khoá TẬP TÊN KHOÁ CẤP MỘT — không kiểu, không giá trị. Danh sách kỳ vọng là chuỗi
/// VIẾT CỨNG (không <c>nameof</c>, không reflection).</para>
/// </summary>
public class CampaignResponseJsonContractUx3B3Tests
{
    // Mirror của Program.cs:142 `AddControllers().AddJsonOptions(...)`. ASP.NET MVC khởi tạo
    // JsonOptions.JsonSerializerOptions = new(JsonSerializerDefaults.Web) ⇒ camelCase; Campaign thêm
    // JsonStringEnumConverter (BK20) + UtcDateTimeConverter. Converter đổi GIÁ TRỊ, không đổi KHOÁ —
    // vẫn thêm cho khớp options runtime. KHÔNG dựng `new JsonSerializerOptions()` trần.
    private static JsonSerializerOptions RuntimeOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    private static void AssertFrozenKeys(object dto, string dtoName, string[] expected)
    {
        var json = JsonSerializer.Serialize(dto, dto.GetType(), RuntimeOptions());
        var actual = ((JsonObject)JsonNode.Parse(json)!)
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            actual.SequenceEqual(expected),
            $"Tên khoá JSON của {dtoName} đã lệch hợp đồng.\n" +
            "Đổi tên khoá JSON là ĐỔI HỢP ĐỒNG. Nếu cố ý, cập nhật danh sách trong test NÀY " +
            "VÀ báo cho bên frontend (họ đang đọc đúng những tên cũ).\n\n" +
            "Kỳ vọng : [" + string.Join(", ", expected) + "]\n" +
            "Thực tế : [" + string.Join(", ", actual) + "]\n\n" +
            "Danh sách mới (dán vào nếu ĐÚNG Ý ĐỒ):\n" +
            string.Join("\n", actual.Select(k => $"        \"{k}\",")));
    }

    // ── HỢP ĐỒNG: khoá JSON cấp một của CampaignResponse. Sắp xếp. Chuỗi VIẾT CỨNG.
    //    ⚠ ĐỔI DANH SÁCH NÀY = ĐỔI HỢP ĐỒNG VỚI FRONTEND.
    private static readonly string[] CampaignResponseKeys =
    [
        "adaptiveEnabled",
        "antiCheatEnabled",
        "createdAt",
        "criteria",
        "criteriaText",
        "cvPolicyVersion",
        "domain",
        "expiresAt",
        "faceVerifyEnabled",
        "groundingEnabled",
        "id",
        "interviewPolicyVersion",
        "jdFileUrl",
        "jdText",
        "jobNeeds",
        "keywordsAny",
        "language",
        "maxCandidates",
        "maxConcurrentInterviews",
        "maxDeepPerQuestion",
        "maxFollowUps",
        "maxQuestions",
        "minYearsExperience",
        "orgId",
        "passScorePct",
        "questionBank",
        "questions",
        "questionsPerSession",
        "requiredSkills",
        "rubricVersion",
        "rubricVersionUpdatedAt",
        "rubricVersionUpdatedBy",
        "seniority",
        "skipPenalty",
        "startsAt",
        "status",
        "timeLimitMinutes",
        "title",
        "updatedAt",
    ];

    // ── HỢP ĐỒNG: khoá JSON cấp một của CampaignListItemResponse (GET /campaign — DANH SÁCH).
    //    Khác CampaignResponse: KHÔNG có questions/criteria/jdText; CÓ 3 số đếm cvCount/invitedCount/completedCount.
    private static readonly string[] CampaignListItemKeys =
    [
        "adaptiveEnabled",
        "antiCheatEnabled",
        "completedCount",
        "createdAt",
        "criteriaText",
        "cvCount",
        "cvPolicyVersion",
        "domain",
        "expiresAt",
        "faceVerifyEnabled",
        "groundingEnabled",
        "id",
        "interviewPolicyVersion",
        "invitedCount",
        "jdFileUrl",
        "jobNeeds",
        "keywordsAny",
        "language",
        "maxCandidates",
        "maxConcurrentInterviews",
        "maxDeepPerQuestion",
        "maxFollowUps",
        "maxQuestions",
        "minYearsExperience",
        "orgId",
        "passScorePct",
        "questionBank",
        "questionsPerSession",
        "requiredSkills",
        "rubricVersion",
        "rubricVersionUpdatedAt",
        "rubricVersionUpdatedBy",
        "seniority",
        "skipPenalty",
        "startsAt",
        "status",
        "timeLimitMinutes",
        "title",
        "updatedAt",
    ];

    [Fact]
    public void CampaignResponse_TopLevelJsonKeys_MatchFrozenContract()
        => AssertFrozenKeys(new CampaignResponse(), nameof(CampaignResponse), CampaignResponseKeys);

    [Fact]
    public void CampaignListItemResponse_TopLevelJsonKeys_MatchFrozenContract()
        => AssertFrozenKeys(new CampaignListItemResponse(), nameof(CampaignListItemResponse), CampaignListItemKeys);
}
