using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// MIS1-B4/REC1-B6 — trích tối đa <c>MaxCriteria</c> × <c>MaxMistakesPerCriterion</c> LỖI SAI cụ
/// thể (không phải trích dẫn nguyên câu như <see cref="RoadmapEvidenceLoader"/>) làm nguyên liệu
/// cho AI gom vào milestone (MIS1-B2) và anchor bài giảng (MIS1-B3). REC1-B6: hai trần này nay
/// BÁM THEO <c>scope</c> (xem <see cref="ScopeCaps"/>) thay vì luôn cứng 4×3=12 — trước bản vá,
/// scope Quick (trần hiển thị 4 bài — REC1-B5) vẫn được nạp đủ 12 lỗi rồi 8/12 BỊ CẮT ÂM THẦM ở
/// tầng AIService (<c>truncate_to_scope</c>), đúng thứ REC1-B5 sinh ra để chống nhưng lại tái diễn
/// ở TẦNG NẠP thay vì tầng trình bày.
///
/// <c>mistake_key</c> ("m1".."mN") MINT Ở ĐÂY, MỘT LẦN, theo ĐÚNG thứ tự đã sort (REC1-B1: tiêu
/// chí TÁI PHẠM NHIỀU BUỔI NHẤT trước — <c>WeakSessions</c> giảm dần, hoà thì <c>Percentage</c>
/// tăng dần làm tie-break phụ; trong mỗi tiêu chí answer ĐIỂM THẤP NHẤT trước, tie-break AnswerId).
/// Không nơi nào khác được re-derive key này từ chỉ số mảng — <c>RoadmapMistake.MistakeKey</c> lưu
/// nguyên chuỗi. Đổi thứ tự sort ⇒ đổi thứ tự "m1".."mN" gắn cho từng lỗi — ĐÚNG và có chủ đích:
/// key chỉ định danh trong phạm vi MỘT lộ trình, không phải hằng số toàn cục. REC1-B6 KHÔNG đụng
/// luật sort này — trần thấp hơn (Quick) chỉ CẮT BỚT đuôi danh sách ĐÃ sort, không đổi thứ tự:
/// lỗi bị cắt luôn là lỗi ÍT TÁI PHẠM NHẤT (cuối danh sách), không phải model/loader tuỳ ý bỏ.
///
/// CHƯA được ai gọi (B5 sẽ nối vào <see cref="RoadmapService.CreateAsync"/>) — hàm này CHỈ trích,
/// KHÔNG <c>Add</c>/<c>SaveChanges</c> — caller quyết định lúc nào persist.
/// </summary>
public static class RoadmapMistakeLoader
{
    /// <summary>Trần Standard (mặc định, giữ nguyên giá trị từ trước REC1-B6) — tối đa bao nhiêu
    /// tiêu chí yếu được trích lỗi, tiêu chí TÁI PHẠM NHIỀU BUỔI NHẤT trước (REC1-B1). ⚠ Đây KHÔNG
    /// còn là trần DUY NHẤT kể từ REC1-B6 — xem <see cref="ScopeCaps"/> cho trần THẬT theo từng
    /// scope; hằng số này giữ lại làm giá trị tham chiếu/tài liệu cho scope Standard.</summary>
    public const int MaxCriteria = 4;

    /// <summary>Trần Standard (mặc định) — tối đa bao nhiêu lỗi/tiêu chí, answer ĐIỂM THẤP NHẤT
    /// trước. Cùng ghi chú với <see cref="MaxCriteria"/> — xem <see cref="ScopeCaps"/>.</summary>
    public const int MaxMistakesPerCriterion = 3;

    /// <summary>
    /// REC1-B6 — trần (<c>MaxCriteria</c>, <c>MaxMistakesPerCriterion</c>) THEO SCOPE. Quick 2×2=4
    /// lỗi (khớp trần hiển thị 2 milestone × 2 lesson của REC1-B5); Standard 4×3=12 (mặc định,
    /// hành vi TRƯỚC bản vá này — giữ nguyên byte-for-byte).
    ///
    /// 🔴 Ý ĐỊNH của ràng buộc "không nhận tham số tuỳ ý từ caller" (docstring <see cref="LoadAsync"/>)
    /// là chặn caller tự đẩy MỘT SỐ NGUYÊN bất kỳ vào đây (ví dụ gọi "nạp cho tôi 50 lỗi") — KHÔNG
    /// phải chặn scope. <c>scope</c> là enum ĐÓNG đúng 2 giá trị, đã được caller sản xuất (
    /// <c>RoadmapService.ValidateScope</c>, BE-4) chuẩn hoá FAIL-CLOSED (400 nếu lạ) TRƯỚC khi tới
    /// đây — nên về nguyên tắc chuỗi lạ không bao giờ chạm được dict này. Bảng tra vẫn fail-OPEN về
    /// Standard cho chuỗi không nhận diện được (mẫu <c>app.roadmap_quality.normalize_scope</c> phía
    /// AIService) làm lớp phòng thủ THỨ HAI — phòng khi loader này được gọi từ một nơi khác trong
    /// tương lai mà quên validate trước; trích lỗi cho roadmap không đáng làm hỏng cả yêu cầu chỉ
    /// vì một chuỗi scope lạ. DÙ VẬY — chuỗi lạ CHỈ có thể rơi về MỘT trong hai cặp số cố định ở
    /// đây, KHÔNG BAO GIỜ ra một trần tuỳ ý khác.
    /// </summary>
    private static readonly Dictionary<string, (int MaxCriteria, int MaxMistakesPerCriterion)> ScopeCaps =
        new(StringComparer.Ordinal)
        {
            ["Quick"] = (2, 2),
            ["Standard"] = (MaxCriteria, MaxMistakesPerCriterion),
        };

    private static (int MaxCriteria, int MaxMistakesPerCriterion) CapsFor(string scope) =>
        ScopeCaps.TryGetValue(scope, out var caps) ? caps : ScopeCaps["Standard"];

    /// <summary>
    /// Chọn tối đa <c>MaxCriteria</c> (theo <paramref name="scope"/> — xem <see cref="ScopeCaps"/>)
    /// tiêu chí TÁI PHẠM NHIỀU BUỔI NHẤT trong <paramref name="weaknesses"/> (đã là tập
    /// <c>NeedsImprovement</c> — "tái phạm nhiều TRONG SỐ đã yếu", không phải toàn cục). REC1-B1:
    /// xếp theo <c>WeakSessions</c> giảm dần (số buổi bị gắn cờ, không phải điểm ở một buổi) —
    /// "yếu 3/4 buổi" đứng trước "yếu 1/4 buổi" dù <c>Percentage</c> (điểm buổi mới nhất) của mục
    /// thứ hai có thấp hơn; <c>Percentage</c> tăng dần chỉ làm tie-break khi <c>WeakSessions</c>
    /// bằng nhau. Với mỗi tiêu chí, tải tối đa <c>MaxMistakesPerCriterion</c> (theo scope) answer
    /// <c>Ai</c>-scoring dưới ngưỡng <paramref name="thresholdPct"/> trong đúng
    /// <paramref name="sessionIds"/>, điểm THẤP NHẤT trước.
    ///
    /// Khớp theo <c>CriterionId</c> (KHÔNG khớp theo tên — tên là snapshot điểm-tại-thời-điểm, còn
    /// rubric_criteria là giá trị SỐNG hiện đang sửa được; admin đổi tên tiêu chí sẽ làm khớp-theo-tên
    /// gãy vĩnh viễn). Bỏ tiêu chí <c>DeliveryMetrics</c> (chấm bằng VAD, không phải văn bản — trình
    /// bày cho người học như "bạn đã nói X" là vô nghĩa). Bỏ answer <c>Skipped</c>/transcript rỗng.
    ///
    /// Trần bám <paramref name="scope"/> (REC1-B6) — KHÔNG nhận tham số tuỳ ý từ caller (chỉ nhận
    /// scope, không nhận số nguyên trực tiếp; xem <see cref="ScopeCaps"/> cho ranh giới chính xác
    /// của ràng buộc này).
    /// </summary>
    public static async Task<IReadOnlyList<RoadmapMistake>> LoadAsync(
        InterviewDbContext db,
        Guid roadmapId,
        IReadOnlyList<Guid> sessionIds,
        IReadOnlyList<RoadmapWeakness> weaknesses,
        decimal thresholdPct,
        string scope,
        CancellationToken ct)
    {
        if (sessionIds.Count == 0 || weaknesses.Count == 0) return [];

        var (maxCriteria, maxMistakesPerCriterion) = CapsFor(scope);

        var result = new List<RoadmapMistake>();
        var seq = 0;

        // REC1-B1 — TÁI PHẠM (WeakSessions) trước, điểm-một-buổi (Percentage) chỉ tie-break.
        // REC1-B6 — .Take(maxCriteria) theo scope: Quick cắt còn 2 tiêu chí đầu (tái phạm nhiều
        // nhất), KHÔNG đổi thứ tự đã sort — tiêu chí bị cắt luôn là tiêu chí ÍT TÁI PHẠM NHẤT.
        foreach (var w in weaknesses
                     .OrderByDescending(x => x.WeakSessions).ThenBy(x => x.Percentage)
                     .Take(maxCriteria))
        {
            // CriterionIds nullable (rubric_criteria có Version — 1 tên có thể ứng nhiều id qua các
            // buổi). Không có id nào để lọc theo → bỏ qua tiêu chí này, KHÔNG khớp theo tên.
            if (w.CriterionIds is not { Count: > 0 }) continue;
            var ids = w.CriterionIds;

            var rows = await db.AnswerScores.AsNoTracking()
                .Where(s => s.AttemptNo == 1 // chấm CHUẨN (temperature=0) — bỏ self-consistency E10
                            && sessionIds.Contains(s.Answer.SessionId)
                            && ids.Contains(s.CriterionId)
                            && s.Criterion.ScoringMethod == CriterionScoringMethod.Ai
                            // Nhân chéo — KHÔNG chia (s.Score*100/s.Criterion.MaxScore): Postgres
                            // không đảm bảo thứ tự đánh giá vế AND, guard MaxScore>0 không chắc chạy
                            // trước phép chia. Nhân chéo hết cửa chia-0, khỏi cần guard ở SQL.
                            && s.Score * 100m < thresholdPct * s.Criterion.MaxScore
                            && s.Reasoning != null && s.Reasoning != ""
                            && s.Answer.Status != AnswerStatus.Skipped
                            && s.Answer.Transcript != null && s.Answer.Transcript != "")
                .OrderBy(s => s.Score).ThenBy(s => s.AnswerId)
                .Take(maxMistakesPerCriterion)
                .Select(s => new
                {
                    s.AnswerId,
                    s.CriterionId,
                    CriterionName = s.Criterion.Name,
                    Question = s.Answer.Question.Content,
                    Answer = s.Answer.Transcript!,
                    Reasoning = s.Reasoning!,
                    SampleAnswer = s.Answer.SampleAnswer,
                    s.Score,
                    MaxScore = s.Criterion.MaxScore,
                    // REC1-B2 mục B — SNAPSHOT trình độ buổi luyện lỗi này bám, NGAY TRONG cùng
                    // truy vấn (PracticeAnswer.Session là nav CÓ SẴN, s.Answer.SessionId đã dùng ở
                    // trên) — không thêm round-trip. PHẢI snapshot ở đây, KHÔNG join lúc đọc: đọc
                    // lại qua `answer.Session.Seniority` sẽ mất mức khi buổi luyện gốc bị xoá
                    // (answer_id là FK SetNull — xem Entities/RoadmapMistake.cs).
                    Seniority = s.Answer.Session.Seniority,
                })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                seq++;
                result.Add(new RoadmapMistake
                {
                    Id = Guid.NewGuid(),
                    RoadmapId = roadmapId,
                    MistakeKey = $"m{seq}",
                    CriterionId = r.CriterionId,
                    CriterionName = r.CriterionName,
                    AnswerId = r.AnswerId,
                    Question = r.Question,
                    Answer = r.Answer,
                    Reasoning = r.Reasoning,
                    SampleAnswer = r.SampleAnswer,
                    // C# side, SAU khi đã materialize (.ToListAsync xong) — KHÔNG dịch xuống SQL nên
                    // guard chia-0 ở đây an toàn, khác vế lọc SQL phía trên phải né chia bằng nhân chéo.
                    ScorePct = r.MaxScore > 0 ? r.Score * 100m / r.MaxScore : 0m,
                    ThresholdPct = thresholdPct,
                    Seniority = r.Seniority,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        return result;
    }
}
