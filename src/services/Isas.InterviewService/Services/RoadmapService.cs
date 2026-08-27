using System.Text;
using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC12 (D20) — roadmap ôn tập cá nhân hoá B2C. Gom điểm yếu (session_criterion_scores, BC9) + CV
// → AIService /generate-roadmap (sync) → LƯU 3 bảng (AI KHÔNG ghi DB). Tạo roadmap KHÔNG trừ credit
// (D7/D15 — chỉ session luyện BC14 mới reserve). AI lỗi → 502, KHÔNG lưu gì (gọi AI trước khi Add).
// BC17 — candidate CHỌN nguồn: SessionIds (baseline), CvAnalysisId + PriorRoadmapId + Focus (bối cảnh
// prompt, KHÔNG vào baseline). Rỗng SessionIds → roadmap CHUẨN theo level (thôi tự gom mọi buổi Scored).
public class RoadmapService : IRoadmapService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly IKnowledgeService? _knowledge;   // RAG grounding — precompute (null = tắt)
    private readonly GroundingOptions _grounding;     // RAG grounding — Enabled/TopK/threshold
    private readonly ILogger<RoadmapService> _logger;
    private readonly IEntitlementClient? _entitlements;
    private readonly RoadmapOptions _roadmap;         // ngưỡng buổi tối thiểu cho chế độ ôn tập
    private readonly bool _tieringEnabled;
    private readonly bool _bilingualEnabled;
    // MIS1-B4 — ngưỡng CÙNG cấu hình mà RoadmapLessonService.cs dùng để tính weaknesses (BC9/E10);
    // RoadmapMistakeLoader (B5) lọc answer dưới ngưỡng NÀY, không phải một ngưỡng riêng.
    private readonly ScoringOptions _scoring;

    public RoadmapService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapService> logger,
        // RAG grounding — optional (default null/tắt): test cũ dựng 4 tham số vẫn compile + precompute tắt.
        IKnowledgeService? knowledge = null,
        IOptions<GroundingOptions>? groundingOptions = null,
        IEntitlementClient? entitlements = null,
        IConfiguration? config = null,
        IOptions<RoadmapOptions>? roadmapOptions = null,
        IOptions<ScoringOptions>? scoringOptions = null)
    {
        _db = db;
        _storage = storage;
        _generator = generator;
        _knowledge = knowledge;
        _grounding = groundingOptions?.Value ?? new GroundingOptions();
        _logger = logger;
        _entitlements = entitlements;
        _roadmap = roadmapOptions?.Value ?? new RoadmapOptions();
        _tieringEnabled = bool.TryParse(config?["Tiering:Enabled"], out var enabled) && enabled;
        _bilingualEnabled = bool.TryParse(config?["Interview:Bilingual:Enabled"], out var bilingual) && bilingual;
        _scoring = scoringOptions?.Value ?? new ScoringOptions();
    }

    public async Task<RoadmapResponse> CreateAsync(
        Guid candidateId, CreateRoadmapRequest req, CancellationToken ct = default)
    {
        var language = ValidateLanguage(req.Language);
        // BE-6 — chuẩn hoá tên NGAY ĐẦU HÀM, trước cả kiểm gói và lời gọi AI: tên sai là lỗi đầu vào,
        // để nó nổ sau khi đã đốt một lượt Gemini là bắt người dùng trả giá cho lỗi gõ của mình.
        // Cùng lý do `ValidateLanguage` đứng ở đây — và cùng lý do `ValidateScope` (BE-4) đứng ngay dưới.
        var requestedName = RoadmapNaming.Normalize(req.Name);
        var createdAt = DateTime.UtcNow;
        var scope = ValidateScope(req.Scope);
        // Cùng lý do đã nêu cho `ValidateLanguage`/`ValidateScope`: lỗi đầu vào phải nổ TRƯỚC khi
        // đốt một lượt Gemini.
        var mode = ValidateMode(req.Mode);

        // MIS1-B6 — GUARD 1: roadmap nay XÂY TỪ LỖI THẬT (MIS1-B4/B5), nên không còn nhánh "roadmap
        // CHUẨN theo level" khi không chọn buổi nào — thiếu buổi = không có gì để xây. Đặt TRƯỚC lời
        // gọi I/O đầu tiên (entitlement) — cùng lý do các `Validate*` ở trên: lỗi đầu vào phải nổ
        // TRƯỚC khi đốt một lượt Gemini hay chạm mạng.
        //
        // Mã lỗi PREFIX (ROADMAP_...) đứng trước câu tiếng Việt để frontend so khớp MÃ, không phải
        // văn xuôi: ba loại 400 của Guard 1/2/3 khác nhau về việc người dùng phải làm (luyện thêm /
        // chọn buổi khác / bớt buổi) — so khớp câu chữ sẽ vỡ khi đổi UI string hoặc đổi ngôn ngữ.
        if (req.SessionIds is not { Count: > 0 })
            throw new InvalidOperationException(
                "ROADMAP_SESSIONS_REQUIRED: Lộ trình được xây từ chính những chỗ bạn còn hụt trong " +
                "các buổi luyện đã chấm, nên cần chọn ít nhất một buổi đã có kết quả. Hãy luyện một " +
                "buổi rồi quay lại.");
        // Trần — mã RIÊNG với ca "chưa chọn buổi nào" ngay trên: dùng chung mã thì người chọn 25
        // buổi sẽ nhận nhầm câu "bạn chưa chọn buổi luyện nào".
        if (req.SessionIds.Count > MaxSourceSessions)
            throw new InvalidOperationException(
                $"ROADMAP_TOO_MANY_SESSIONS: Một lộ trình chỉ xây được từ tối đa {MaxSourceSessions} " +
                $"buổi luyện (đang chọn {req.SessionIds.Count}). Hãy bớt bớt rồi chọn lại.");

        if (_tieringEnabled && _entitlements is not null && !(await _entitlements.ResolveUserAsync(candidateId, ct)).RoadmapEnabled)
            throw new UnauthorizedAccessException("Gói hiện tại không bao gồm roadmap ôn tập.");
        // 🔴 REC1-B7 — `req.CvId` KHÔNG còn được kiểm chủ sở hữu ở đây (đã gỡ lời gọi
        // `ReadOwnedParsedTextAsync`, vốn CHỈ kiểm quyền rồi vứt nội dung `_ =` — nội dung CV đã
        // ngừng đi vào prompt từ trước bước này). `CvId` VẪN được lưu xuống `roadmaps.cv_id`
        // (FK Restrict → file_records, xem gán `CvId = req.CvId` bên dưới) — id không tồn tại sẽ bị
        // chính ràng buộc FK đó chặn ở SaveChanges, chỉ là KHÔNG còn câu lỗi 404/403 thân thiện
        // riêng cho trường hợp này. `CvAnalysisId`/`PriorRoadmapId`/`CurrentLevel` cũng cùng số phận
        // (gỡ toàn bộ guard 404/403/400 phía dưới) — bốn field này giờ chỉ còn Ý NGHĨA LƯU TRỮ
        // (CvId) hoặc BỊ BỎ QUA HOÀN TOÀN (ba field kia), không còn validate/dùng làm ngữ cảnh
        // prompt. Lý do: prompt roadmap chỉ xuất ra CẤU TRÚC, mà cả CV lẫn lộ trình trước đều bị
        // chèn kèm câu "không đổi cấu trúc roadmap" — mệnh lệnh tự phủ định. Đo được: nhóm CÓ chọn
        // CV nêu công nghệ cụ thể ÍT hơn (8,6% vs 12,1%); lộ trình trước chỉ 4/37 đủ điều kiện trên
        // dev, 0 trên môi trường chính. DTO GIỮ NGUYÊN cả 4 field (expand/contract — dọn ở đợt sau
        // khi frontend ngừng gửi).

        // BC17 — baseline lấy từ CÁC BUỔI CANDIDATE CHỌN (thôi tự gom MỌI buổi Scored). MIS1-B6 —
        // GUARD 1 (ngay trên) đã đảm bảo `req.SessionIds` LUÔN {Count: > 0} tới đây; khối `if` dưới
        // đây giờ luôn vào — GIỮ NGUYÊN dạng `if` (không tháo dỡ) để không phải viết lại/thụt lề toàn
        // bộ thân hàm cho một thay đổi ngoài phạm vi bước này.
        Dictionary<string, decimal>? baseline = null;
        List<RoadmapWeakness>? weaknesses = null;
        List<Guid>? sourceSessionIds = null;
        // REC1-B2 mục A — mức LỘ TRÌNH SUY từ buổi nguồn, KHÔNG còn là lời tự khai của `req.Level`
        // (đo trên production: chỉ 4/61 buổi đạt ngưỡng cấp của chính mình). `DefaultRoadmapLevel`
        // chỉ dùng khi `chosen` rỗng — về lý thuyết không xảy ra ở đây (Guard 1 phía trên đã ép
        // `req.SessionIds` luôn {Count:>0}, và `chosen.Count == requestedIds.Count` vừa được assert
        // bên trong khối `if` ngay dưới) nhưng giữ làm phòng thủ, không phải đường thật.
        var roadmapLevel = DefaultRoadmapLevel;

        if (req.SessionIds is { Count: > 0 })
        {
            var requestedIds = req.SessionIds.Distinct().ToList();

            // CHỈ những buổi được chọn, owner-scoped + B2C + đã Scored (BC-3). Không phủ đủ MỌI id yêu
            // cầu → 404 batch (KHÔNG lộ id nào thiếu / không thuộc mình / chưa chấm).
            //
            // Vế "B2C + đã Scored" tách riêng thành .Where(RoadmapSessionEligibility.Predicate) —
            // MỘT nguồn sự thật dùng chung với PracticeService.GetHistoryAsync (wizard picker gọi
            // ?status=Scored&excludeCampaign=true). Gộp chung một Where() với id/owner thì vẫn dịch
            // đúng SQL (EF AND các vị từ), nhưng tách riêng biểu thức là thứ cho phép hai nơi CHIA
            // SẺ cùng một object thay vì chép tay hai lần.
            var chosen = await _db.PracticeSessions.AsNoTracking()
                .Include(s => s.CriterionScores)
                .Where(s => requestedIds.Contains(s.Id) && s.CandidateId == candidateId)
                .Where(RoadmapSessionEligibility.Predicate)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct);

            if (chosen.Count != requestedIds.Count)
                throw new KeyNotFoundException(
                    "Một số buổi luyện không tồn tại, không thuộc về bạn, hoặc chưa được chấm.");

            // 🔴 Buổi luyện phải CÙNG NGHỀ với lộ trình đang tạo. Guard sở hữu ở trên KHÔNG có vế
            // này, nên trước bản vá `POST /practice/roadmaps {jobCategory:"BE"}` kèm sessionIds của
            // buổi BA vẫn trả 201: baseline/điểm yếu/bằng chứng của nghề KHÁC chảy thẳng vào prompt,
            // trong khi `LoadCriteriaNamesAsync` bên dưới lại nạp tiêu chí của BE ⇒ AI được yêu cầu
            // bám một bộ tiêu chí không hề sinh ra con số nào nó đang đọc. Frontend đã lọc phía
            // client, nhưng UI giấu đi ≠ hợp đồng từ chối.
            //
            // 🔴 Vị trí BẮT BUỘC là ở ĐÂY — sau guard sở hữu, TRƯỚC guard `Reinforce` bên dưới:
            //   • sau guard sở hữu, vì thông báo này NÊU ĐÍCH DANH id, mà chỉ những id đã chứng minh
            //     thuộc người gọi mới được phép nêu tên. Guard sở hữu cố ý gộp "không tồn tại /
            //     không thuộc mình / chưa chấm" vào MỘT câu 404 câm để không lộ id nào của người
            //     khác tồn tại — nói về một id chưa qua cửa đó là mở lại đúng lỗ rò ấy.
            //   • trước guard `Reinforce`, vì buổi lệch nghề mà tình cờ không có tiêu chí nào cần
            //     cải thiện sẽ nhận câu "hãy chọn buổi khác" — đúng lời khuyên nhưng SAI nguyên
            //     nhân, người dùng đi chọn thêm buổi BA nữa và vẫn hỏng.
            var crossCategory = chosen.Where(s => s.JobCategory != req.JobCategory).ToList();
            if (crossCategory.Count > 0)
                throw CrossCategorySource(
                    "Buổi luyện đã chọn", req.JobCategory,
                    string.Join("; ", crossCategory
                        .GroupBy(s => s.JobCategory)
                        .OrderBy(g => g.Key)
                        .Select(g => $"{g.Key} ({string.Join(", ", g.Select(s => s.Id))})")));

            // MIS1-B6 — GUARD NGÔN NGỮ: cùng vị trí (sau guard sở hữu, để nêu đích danh id an toàn)
            // và cùng lý do với guard lệch nghề ngay trên. Buổi tiếng Anh làm nguồn cho lộ trình
            // tiếng Việt sẽ trích NGUYÊN VĂN câu hỏi/lý do tiếng Anh (RoadmapMistakeLoader) vào bài
            // giảng tiếng Việt — khác lệch nghề (chỉ sai TÊN tiêu chí bám vào), lệch ngôn ngữ làm
            // toàn bộ nội dung trích ra sai NGÔN NGỮ của bài giảng.
            var languageMismatch = chosen.Where(s => s.Language != language).ToList();
            if (languageMismatch.Count > 0)
                throw LanguageMismatchSource(
                    "Buổi luyện đã chọn", language,
                    string.Join("; ", languageMismatch
                        .GroupBy(s => s.Language)
                        .OrderBy(g => g.Key)
                        .Select(g => $"{g.Key} ({string.Join(", ", g.Select(s => s.Id))})")));

            // REC1-B2 mục A — mức LỘ TRÌNH = mức CAO NHẤT trong các buổi đã chọn. `chosen` đã
            // `.ToListAsync()` ĐẦY ĐỦ ở trên (KHÔNG projection) nên `s.Seniority` nằm sẵn trong bộ
            // nhớ — KHÔNG thêm truy vấn nào. `chosen.Count > 0` LUÔN đúng tới đây (đã assert ở guard
            // sở hữu phía trên `chosen.Count == requestedIds.Count`, và `requestedIds` không rỗng
            // theo Guard 1) — nhánh else giữ `DefaultRoadmapLevel` chỉ là phòng thủ.
            //
            // `PracticeSession.Seniority` là string TỰ DO ở tầng entity nhưng bị CHECK
            // `ck_practice_sessions_seniority`/`PracticeService.AllowedSeniorities` ép về đúng 4
            // tên (Fresher/Junior/Middle/Senior) trùng khít thứ tự tăng dần của `RoadmapLevel` —
            // `Enum.Parse` ở đây an toàn, không phải xấp xỉ.
            if (chosen.Count > 0)
                roadmapLevel = chosen.Select(s => Enum.Parse<RoadmapLevel>(s.Seniority)).Max();

            // Newest-first: tiêu chí xuất hiện lần đầu (buổi mới nhất) thắng → baseline = % hiện tại.
            //
            // MIS1-B4 — `CriterionIds` KHÔNG thể lấy theo cùng luật "chỉ buổi mới nhất": rubric_criteria
            // có Version + custom-per-candidate (BC16), nên "cùng một TÊN tiêu chí" ở hai buổi khác
            // nhau có thể mang hai ID KHÁC NHAU (đổi version rubric, hoặc chuyển rubric riêng giữa
            // các buổi). Lấy 1 id (của buổi mới nhất) sẽ âm thầm bỏ sót RoadmapMistakeLoader của
            // những buổi mang id khác — 0 lỗi, 0 cảnh báo, một nhánh hiếm gặp lặng lẽ thành đường
            // chính. `RoadmapWeakness` là record bất biến ⇒ BẮT BUỘC 2 lượt: gom hết id ở lượt 1 rồi
            // mới dựng record ở lượt 2 (không patch được record đã tạo).
            var withScores = chosen.Where(s => s.CriterionScores.Count > 0).ToList();
            if (withScores.Count > 0)
            {
                baseline = new Dictionary<string, decimal>();
                var criterionIdsByName = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
                var weakNamesInOrder = new List<string>();
                var weakNamesSeen = new HashSet<string>(StringComparer.Ordinal);
                // REC1-B1 — đếm SỐ BUỔI (trong withScores) mà tiêu chí này bị NeedsImprovement, trên
                // MỌI buổi — cùng nguồn dữ liệu quyết định CÓ CHỌN tiêu chí vào weaknesses hay không
                // (xem nhánh if ngay dưới, tách khỏi guard first-seen của baseline).
                var weakCountByName = new Dictionary<string, int>(StringComparer.Ordinal);

                // Lượt 1 — gom SONG SONG, NHƯNG hai việc KHÔNG còn cùng một guard nữa:
                //   • baseline[name]   — first-seen THẮNG (buổi MỚI NHẤT trong vòng lặp newest-first
                //                        này) → vẫn là "% hiện tại", giữ nguyên luật cũ.
                //   • weak/weakCount   — CHẠY TRÊN MỌI buổi, không bị chặn bởi baseline đã có hay
                //                        chưa. REC1-B1: trước bản vá, `continue` ở dưới chặn LUÔN cả
                //                        việc đọc NeedsImprovement của mọi buổi CŨ HƠN buổi đã set
                //                        baseline ⇒ tiêu chí yếu 3 buổi trước mà buổi mới nhất ổn
                //                        KHÔNG BAO GIỜ vào lộ trình — không lỗi, không cảnh báo.
                // CriterionIds vẫn gom từ MỌI buổi như cũ (không đổi).
                foreach (var s in withScores)
                    foreach (var cs in s.CriterionScores)
                    {
                        if (!criterionIdsByName.TryGetValue(cs.CriterionName, out var ids))
                            criterionIdsByName[cs.CriterionName] = ids = [];
                        ids.Add(cs.CriterionId);

                        if (cs.NeedsImprovement)
                        {
                            weakCountByName[cs.CriterionName] =
                                weakCountByName.GetValueOrDefault(cs.CriterionName) + 1;
                            if (weakNamesSeen.Add(cs.CriterionName))
                                weakNamesInOrder.Add(cs.CriterionName);
                        }

                        if (baseline.ContainsKey(cs.CriterionName)) continue;
                        baseline[cs.CriterionName] = cs.Percentage;
                    }

                // Lượt 2 — dựng RoadmapWeakness (bất biến) từ các dict đã gom XONG ở lượt 1.
                // `baseline[name]` LUÔN có giá trị cho mọi name trong weakNamesInOrder: lần đầu gặp
                // một tên (dù có NeedsImprovement hay không) đều rơi xuống nhánh set-baseline ngay
                // trong cùng vòng lặp phía trên — không có đường nào một tên vào được weakNamesInOrder
                // mà baseline chưa từng thấy nó.
                // TotalSessions = withScores.Count CỐ ĐỊNH cho mọi mục — cỡ mẫu của CẢ lộ trình,
                // không phải "số buổi tiêu chí này từng xuất hiện" (khác WeakSessions, vốn LÀ theo
                // từng tiêu chí).
                var weak = weakNamesInOrder
                    .Select(name => new RoadmapWeakness(
                        name, baseline[name], criterionIdsByName[name],
                        weakCountByName.GetValueOrDefault(name), withScores.Count))
                    .ToList();

                weaknesses = weak.Count > 0 ? weak : null;
            }

            // sourceSessionIds = ĐÚNG các buổi được chọn (đều đã Scored/owned nhờ guard phủ ở trên).
            sourceSessionIds = chosen.Select(s => s.Id).ToList();
        }

        // MIS1-B6 — GUARD 2: roadmap BẮT BUỘC có dữ liệu điểm yếu để xây từ — và khi thiếu thì phải
        // NÓI RA, tuyệt đối không âm thầm sinh một lộ trình rỗng nội dung rồi dán nhãn như bình
        // thường. Trước bước này guard CHỈ chạy cho `Reinforce` (roadmap LevelUp "hợp lý" khi không
        // có điểm yếu — nó vốn không xây từ lỗi thật). Nay roadmap XÂY TỪ LỖI THẬT ở CẢ HAI mode
        // (MIS1-B4/B5) nên guard PHẢI vô điều kiện — thiếu nó, LevelUp không chọn buổi có điểm yếu
        // vẫn "thành công" với một lộ trình không bám gì cụ thể, đúng lớp lỗi "nén im lặng" mà bản
        // gốc của guard này sinh ra để chặn (chọn Thực tập nhận Mới-tốt-nghiệp; chọn Lead nhận Senior).
        //
        // Câu chữ khác Guard 1 (:74 phía trên) có chủ đích — người dùng phải làm HAI việc khác nhau:
        //   • Guard 1 (thiếu BUỔI)      → đi luyện thêm (hoặc chọn thêm buổi cũ vào danh sách),
        //   • Guard 2 (thiếu ĐIỂM YẾU)  → các buổi đó không có tiêu chí nào cần cải thiện, phải
        //     CHỌN BUỔI KHÁC — không phải luyện thêm. Gộp chung một câu sẽ bảo người vừa luyện 5
        //     buổi rất tốt rằng họ "chưa luyện đủ".
        //
        // 🔴 Bỏ nhánh thoát "hoặc tạo lộ trình ở chế độ LevelUp" khỏi câu chữ gốc (guard cũ, chỉ
        // chạy cho Reinforce): LevelUp nay CŨNG bị chặn bởi chính guard này, giữ câu đó sẽ là lời
        // khuyên SAI (đề nghị một lối thoát không tồn tại).
        if (weaknesses is not { Count: > 0 })
            throw new InvalidOperationException(
                "ROADMAP_NO_WEAKNESS: Các buổi luyện đã chọn không có tiêu chí nào bị đánh dấu cần " +
                "cải thiện, nên không có gì để xây lộ trình. Hãy chọn buổi khác.");

        // 🔴 REC1-B7 — khối `CvAnalysisId` (3 guard 404/403/400 + tóm tắt làm ngữ cảnh prompt) và
        // khối `PriorRoadmapId` (4 guard + tóm tắt) đã GỠ HẲN khỏi đây — lý do đầy đủ ở comment
        // đầu hàm (ngay sau guard `RoadmapEnabled`). `req.CvAnalysisId`/`req.PriorRoadmapId`/
        // `req.CurrentLevel` không còn được đọc ở bất kỳ đâu trong hàm này; DTO vẫn khai đủ 3 field
        // (client gửi gì cũng bị bỏ qua, không 404/403/400, không tác dụng).

        // BC17 — mô tả tự do: trim (khoảng-trắng-thuần = không nhập) + cap độ dài → vượt 400. Chống
        // prompt-injection (bọc như dữ liệu) là việc phía AIService (worker Python), không phải ở đây.
        string? focus = null;
        if (!string.IsNullOrWhiteSpace(req.Focus))
        {
            focus = req.Focus.Trim();
            if (focus.Length > FocusMaxChars)
                throw new InvalidOperationException($"Mô tả focus vượt quá {FocusMaxChars} ký tự.");
        }

        // BE-1 — tiêu chí năng lực THẬT của (nghề, ngôn ngữ) này, để milestone.focusCriteria chọn
        // NGUYÊN VĂN thay vì bịa tên (đo trên production: chỉ 7% focusCriteria khớp tên tiêu chí
        // thật khi AIService không được cấp danh sách này).
        var criteria = await LoadCriteriaNamesAsync(candidateId, req.JobCategory, language, ct);

        // MIS1-B5 — id CẤP TRƯỚC (không phải lúc `new Roadmap`) vì RoadmapMistakeLoader/entity
        // RoadmapMistake cần `RoadmapId` NGAY để gắn FK trước khi roadmap được Add. Chưa `SaveChangesAsync`
        // nên chưa có gì ràng buộc ở DB tại thời điểm này (AI lỗi → không Add gì, id vứt đi vô hại).
        var roadmapId = Guid.NewGuid();

        // MIS1-B4/B5 — trích LỖI SAI cụ thể (tối đa 4 tiêu chí yếu nhất × 3 lỗi/tiêu chí — hoặc
        // 2×2 cho scope Quick, REC1-B6) từ các buổi đã chọn, dưới ngưỡng CÙNG cấu hình với
        // NeedsImprovement (BC9/E10) — `_scoring`, KHÔNG một ngưỡng riêng. `scope` ĐÃ chuẩn hoá
        // fail-CLOSED ở `ValidateScope` phía trên — loader tự CapsFor về "Standard" nếu lỡ nhận
        // chuỗi lạ (lớp phòng thủ thứ hai, xem RoadmapMistakeLoader.ScopeCaps).
        var loadedMistakes = await RoadmapMistakeLoader.LoadAsync(
            _db, roadmapId, sourceSessionIds ?? [], weaknesses ?? [], _scoring.ImprovementThresholdPct,
            scope, ct);

        // MIS1-B6 — GUARD 3: Guard 2 chỉ đảm bảo CÓ tiêu chí bị đánh dấu yếu (session_criterion_
        // scores), KHÔNG đảm bảo trích được LỖI NỘI DUNG nào — RoadmapMistakeLoader loại bỏ tiêu chí
        // `DeliveryMetrics` (chấm bằng số đo âm học, không có "câu trả lời hụt" dạng văn bản để
        // trích). Ca "yếu toàn bộ ở cách nói" lọt qua Guard 2 nhưng KHÔNG có gì để roadmap bám vào —
        // phải chặn RIÊNG ở đây, SAU khi loader đã chạy thật (không suy được từ session_criterion_
        // scores, phải hỏi tận answer_scores).
        //
        // 🔴 ĐÃ CÂN NHẮC VÀ CẮT phương án khác: dựng 1 hàng roadmap_mistakes tổng hợp ("d1") chứa
        // số đo cách nói cho AI bám thay. Cắt vì: prod đo 13/13 user có buổi đã chấm đều trích được
        // lỗi nội dung ⇒ nhánh này gần như không chạy; nó lại kéo theo một hợp đồng dây MỚI (số đo
        // phải đi tới tận frontend), một nhánh render toàn null-field bên FE, và một cách gộp số đo
        // chưa ai chốt. Một câu từ chối trung thực rẻ hơn bốn chỗ có thể sai.
        if (loadedMistakes.Count == 0)
            throw new InvalidOperationException(
                "ROADMAP_NO_CONTENT_MISTAKES: Các buổi bạn chọn không có câu trả lời nào hụt về " +
                "nội dung — bạn đang yếu ở cách trình bày, không phải kiến thức. Hãy luyện một buổi " +
                "khó hơn để tìm đúng chỗ hụt.");

        // Guard 3 vừa đảm bảo `loadedMistakes.Count > 0` — không còn nhánh rỗng cần gửi `null`
        // xuống generator nữa (khác lúc `mistakes` mới ra đời ở MIS1-B5, khi caller có thể chưa có
        // gì để gom).
        var mistakesForAi = loadedMistakes;

        // 🔴 MIS1-B5 — `evidence` từng gỡ KHỎI ĐÂY (đi cùng chế độ giáo trình MIS1-B2 đã bỏ):
        // `mistakes` ở trên nay là nguồn GOM CHỦ ĐỀ, giàu hơn evidence (có id để AI trỏ ngược).
        // REC1-B7 đi thêm một bước: tham số `evidence` đã GỠ HẲN khỏi chữ ký `GenerateAsync` này
        // (nó chết sẵn từ MIS1-B5 — không caller nào từng truyền) — khác `GenerateLessonTheoryAsync`
        // vẫn giữ `evidence` nguyên vẹn (`RoadmapEvidenceLoader.cs`/test không đụng, ngoài phạm vi
        // bước này).
        //
        // 🔴 REC1-B7 — `cvAnalysisSummary`/`priorRoadmapSummary`/`currentLevel` cũng GỠ khỏi lời
        // gọi này (cùng lúc với việc gỡ hẳn khỏi chữ ký `GenerateAsync`) — lý do đầy đủ ở comment
        // đầu hàm.
        //
        // Gọi AIService sinh cấu trúc (sync). Lỗi → AiServiceException (502) → KHÔNG lưu gì.
        var ai = language == "vi"
            ? await _generator.GenerateAsync(req.JobCategory.ToString(), roadmapLevel.ToString(), weaknesses,
                focus, criteria, scope, mode: mode, ct: ct, mistakes: mistakesForAi)
            : await _generator.GenerateAsync(req.JobCategory.ToString(), roadmapLevel.ToString(), weaknesses,
                focus, ct, language, criteria, scope, mode: mode, mistakes: mistakesForAi);

        // 🔴 MIS1-B5 — NARROW LẠI Ở .NET: AI tự gán `mistakeIds` khi gom chủ đề (MIS1-B2), nhưng
        // CẤM tin thẳng — id lạ/bịa phải bị lọc trước khi chạm DB (mẫu NarrowToCited của
        // RoadmapLessonService, chống bịa BY-CONSTRUCTION như focusCriteria/BE-1 lẽ ra phải làm ở
        // .NET nhưng hiện chỉ lọc phía Python — KHÔNG lặp lại lỗ đó ở đây: hai service deploy RỜI
        // NHAU, một bản AIService lỗi ghi thẳng id treo là ghi thẳng vào DB InterviewService).
        var validMistakeKeys = new HashSet<string>(
            loadedMistakes.Select(m => m.MistakeKey), StringComparer.Ordinal);

        var roadmap = new Roadmap
        {
            Id = roadmapId,
            CandidateId = candidateId,
            // BE-6 — tên người dùng gửi đã được chuẩn hoá ở ĐẦU hàm (trước lời gọi AI); vắng thì
            // sinh mặc định tại đây để `CreatedAt` dùng cho tên khớp đúng giá trị vừa gán bên dưới.
            Name = requestedName ?? RoadmapNaming.BuildDefault(req.JobCategory, roadmapLevel, language, createdAt),
            JobCategory = req.JobCategory,
            Mode = mode,
            // REC1-B2 mục A — SUY từ buổi nguồn, KHÔNG dùng `req.Level` (client gửi gì cũng bị bỏ qua).
            Level = roadmapLevel,
            Language = language,
            CvId = req.CvId,
            SourceSessionIds = sourceSessionIds,
            Baseline = baseline,
            Status = RoadmapStatus.Active,
            CreatedAt = createdAt
        };

        var milestoneOrder = 1;
        foreach (var m in ai.Milestones)
        {
            var milestone = new RoadmapMilestone
            {
                Id = Guid.NewGuid(),
                OrderNo = milestoneOrder++,
                Title = m.Title,
                FocusCriteria = m.FocusCriteria.ToList(),
                Status = MilestoneStatus.Pending,
                // MIS1-B5 — narrow: null (AI không trả field này) giữ null; có trả (kể cả rỗng sau
                // lọc) → GIỮ milestone với refs rỗng, KHÔNG drop (khác id lạ ở focusCriteria: một
                // milestone không gom được lỗi nào vẫn là milestone hợp lệ ở tầng .NET — quyết định
                // "có nên tồn tại không" đã chốt xong ở AIService/MIS1-B2, .NET chỉ lọc id).
                MistakeRefs = NarrowMistakeRefs(m.MistakeIds, validMistakeKeys)
            };

            var lessonOrder = 1;
            foreach (var l in m.Lessons)
                milestone.Lessons.Add(new RoadmapLesson
                {
                    Id = Guid.NewGuid(),
                    OrderNo = lessonOrder++,
                    Title = l.Title,
                    Status = LessonStatus.Theory,
                    TheoryContent = null,
                    MistakeRefs = NarrowMistakeRefs(l.MistakeIds, validMistakeKeys)
                });

            roadmap.Milestones.Add(milestone);
        }

        // MIS1-B4/B5 — lưu CÙNG transaction với roadmap (roadmap_mistakes.roadmap_id FK Cascade tới
        // `roadmaps`; roadmap AI lỗi ở trên đã throw TRƯỚC khi tới đây nên không có hàng mồ côi).
        if (loadedMistakes.Count > 0)
            _db.Set<RoadmapMistake>().AddRange(loadedMistakes);

        // RAG grounding (Cách 2 — precompute): batch-embed query từng lesson (tên bài + focus milestone +
        // jobCategory) trong 1 lần /embed → Qdrant search → LƯU snapshot vào lesson.GroundingRefs. Lúc MỞ
        // lesson (OpenLessonAsync) feed thẳng snapshot này, KHÔNG retrieve realtime (không thêm độ trễ lazy).
        // best-effort — RetrieveBatchAsync tự degrade rỗng khi lỗi; wrap để có sự cố lạ vẫn KHÔNG chặn tạo roadmap.
        if (_grounding.Enabled && _knowledge is not null)
            await PrecomputeLessonGroundingAsync(roadmap, req.JobCategory.ToString(), ct);

        _db.Set<Roadmap>().Add(roadmap);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC12: roadmap {Id} candidate {CandidateId} ({Cat}/{Level}/{Mode}) milestones={M} sources={S}",
            roadmap.Id, candidateId, req.JobCategory, roadmapLevel, mode,
            roadmap.Milestones.Count, sourceSessionIds?.Count ?? 0);

        // Roadmap vừa tạo — chưa bài nào được làm, nên rỗng là ĐÚNG do cấu trúc (không phải bỏ sót).
        return Map(roadmap, includeTheory: true, attemptCounts: EmptyAttemptCounts, scope: scope);
    }

    // RAG grounding (Cách 2) — precompute snapshot cho MỌI lesson trong roadmap. 1 lần /embed cho tất cả
    // query → search từng lesson → set GroundingRefs (LIST rỗng nếu miss — grounding ĐÃ chạy nên KHÔNG null,
    // phân biệt với roadmap cũ chưa precompute = null). Query = tên bài + focus milestone + jobCategory.
    private async Task PrecomputeLessonGroundingAsync(Roadmap roadmap, string jobCategory, CancellationToken ct)
    {
        // Duyệt lesson theo đúng thứ tự để map kết quả batch về đúng lesson.
        var flat = roadmap.Milestones
            .SelectMany(m => m.Lessons.Select(l => (Lesson: l, Milestone: m)))
            .ToList();
        if (flat.Count == 0) return;

        var queries = flat.Select(x =>
        {
            var parts = new List<string> { x.Lesson.Title, jobCategory };
            if (x.Milestone.FocusCriteria is { Count: > 0 } focus)
                parts.Insert(1, string.Join(", ", focus));
            return string.Join("\n", parts);
        }).ToList();

        IReadOnlyList<IReadOnlyList<GroundingChunk>> batches;
        try
        {
            batches = await _knowledge!.RetrieveBatchAsync(jobCategory, queries, ct);
        }
        catch (Exception ex)
        {
            // RetrieveBatchAsync vốn tự degrade; wrap phòng lỗi lạ → precompute rỗng, KHÔNG chặn tạo roadmap.
            _logger.LogWarning(ex, "RAG grounding: precompute roadmap lỗi → grounding_refs=[] (ungrounded)");
            foreach (var (lesson, _) in flat) lesson.GroundingRefs = new List<GroundingChunk>();
            return;
        }

        for (int i = 0; i < flat.Count; i++)
            // grounding ĐÃ chạy → LUÔN set list (rỗng = ungrounded), KHÔNG để null.
            flat[i].Lesson.GroundingRefs = (batches.Count > i ? batches[i] : Array.Empty<GroundingChunk>()).ToList();

        var grounded = flat.Count(x => x.Lesson.GroundingRefs is { Count: > 0 });
        _logger.LogInformation(
            "RAG grounding: precompute roadmap {Id} — {Grounded}/{Total} lesson có nguồn",
            roadmap.Id, grounded, flat.Count);
    }

    /// <summary>
    /// BE-1 — tên THẬT của mọi tiêu chí năng lực (nghề, ngôn ngữ) đang hiệu lực cho candidate, để
    /// AIService chỉ chọn <c>milestone.focusCriteria</c> bằng cách sao chép NGUYÊN VĂN từ đây thay
    /// vì tự bịa tên.
    ///
    /// Dùng CHUNG <see cref="B2CRubricScope"/> với mọi chỗ chọn tiêu chí B2C khác (publish · callback
    /// · republisher · BC9 · <c>LoadTargetableCriteriaAsync</c> của <c>PracticeService</c>): resolve
    /// khác đi ở đây thì tên gửi cho roadmap trỏ vào một bộ rubric KHÁC bộ dùng để chấm điểm thật.
    ///
    /// KHÔNG lọc theo <see cref="ScoringScope"/> (khác <c>LoadTargetableCriteriaAsync</c> chỉ lấy
    /// <c>WhenTargeted</c>) — roadmap cần TOÀN BỘ tên tiêu chí năng lực (cả 4 tiêu chí CÁCH NÓI lẫn
    /// 3 tiêu chí NỘI DUNG của seed B2C), vì milestone có thể hợp lý nhắm cả hai nhóm.
    /// </summary>
    private async Task<List<QuestionTargetCriterionDto>> LoadCriteriaNamesAsync(
        Guid candidateId, JobCategory jobCategory, string language, CancellationToken ct)
    {
        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, language, ct);
        var query = _db.RubricCriteria.AsNoTracking()
            .Where(c => c.IsActive && c.CampaignId == null
                        && c.JobCategory == jobCategory && c.Language == language);
        query = owner is Guid oid
            ? query.Where(c => c.CandidateId == oid)
            : query.Where(c => c.CandidateId == null);

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new QuestionTargetCriterionDto(c.Id, c.Name))
            .ToListAsync(ct);
    }

    /// <summary>
    /// BE-6 — đổi tên lộ trình. Trả `null` khi không tồn tại (404); khác chủ → 403.
    ///
    /// Cho đổi ở MỌI trạng thái, kể cả `Completed`: tên là nhãn người dùng tự đặt để phân biệt các
    /// lộ trình của mình, không phải dữ liệu kết quả bị đóng băng khi học xong.
    /// </summary>
    public async Task<RoadmapResponse?> RenameAsync(
        Guid candidateId, Guid id, string? requestedName, CancellationToken ct = default)
    {
        // Ở đường ĐỔI TÊN, tên là thứ DUY NHẤT người dùng gửi — nên `null` cũng là đầu vào sai,
        // khác đường TẠO nơi `null` hợp lệ và có nghĩa "để server đặt hộ".
        var name = RoadmapNaming.Normalize(requestedName)
            ?? throw new InvalidOperationException("Tên lộ trình không được để trống.");

        var r = await _db.Set<Roadmap>()
            .Include(x => x.Milestones).ThenInclude(m => m.Lessons)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (r is null) return null;                                        // 404
        if (r.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");   // 403

        r.Name = name;
        await _db.SaveChangesAsync(ct);

        return Map(r, includeTheory: false, await LoadAttemptCountsAsync(id, ct));
    }

    public async Task<RoadmapResponse?> GetAsync(
        Guid candidateId, Guid id, CancellationToken ct = default)
    {
        var r = await _db.Set<Roadmap>().AsNoTracking()
            .Include(x => x.Milestones).ThenInclude(m => m.Lessons)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (r is null) return null;                                        // 404
        if (r.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");   // 403

        return Map(r, includeTheory: true, await LoadAttemptCountsAsync(id, ct));
    }

    /// <summary>
    /// Danh sách roadmap của chính user — keyset-paged (mẫu DB8/DB31), KHÔNG kèm cây milestone/lesson.
    ///
    /// Trước: <c>Include(Milestones).ThenInclude(Lessons)</c> + không phân trang ⇒ payload nhân theo
    /// cây (roadmap × milestone × lesson) và không có trần, cho một màn hình danh sách chỉ vẽ tiêu đề
    /// + ngày + trạng thái. Nay project thẳng <see cref="RoadmapSummaryResponse"/> trong SQL; ai cần
    /// cây đầy đủ thì gọi <c>GET /roadmaps/{id}</c> (giữ nguyên <see cref="RoadmapResponse"/>).
    /// Đã đối chiếu FE trước khi bỏ: trang danh sách không đọc <c>milestones</c> (chỉ trang chi tiết
    /// đọc, và nó gọi endpoint khác).
    /// </summary>
    public async Task<KeysetPage<RoadmapSummaryResponse>> ListAsync(
        Guid candidateId, string? cursor = null, int? limit = null,
        string? status = null, bool? hasFinalReport = null, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.Set<Roadmap>().AsNoTracking()
            .Where(x => x.CandidateId == candidateId);

        // ── Lọc OPT-IN cho picker "chọn lộ trình đã hoàn tất" của wizard ────────────────────
        //
        // 🔑 Lọc PHẢI nằm ở đây, TRƯỚC `Take` — tức trong SQL, không phải sau khi đã lấy trang.
        // Trước bản này client lọc trên TRANG ĐẦU của keyset paging: người có nhiều lộ trình thì
        // cái đã hoàn tất nằm ở trang 2 trở đi BIẾN MẤT khỏi dropdown mà không ai biết — không
        // báo lỗi, không dòng trống, chỉ là một lựa chọn đáng lẽ có mà không thấy.
        var parsedStatus = ValidateRoadmapStatus(status);
        if (parsedStatus is not null)
            query = query.Where(x => x.Status == parsedStatus.Value);

        // ⚠ `hasFinalReport` mới là vị ngữ ĐÚNG NGHIỆP VỤ, không phải `status == Completed`:
        // `CreateAsync` gác bằng `IsNullOrWhiteSpace(prior.FinalReport)` → 400, còn
        // `RoadmapLessonService.RetryLessonAsync` mở lại roadmap `Completed → Active` và XOÁ
        // `FinalReport` ⇒ hai vị ngữ KHÔNG đồng nhất theo cả hai chiều. Lọc theo status là mời một
        // lộ trình rồi để người dùng ăn 400 SAU KHI đã chờ 13–54s tạo roadmap. Giữ cả hai tham số
        // vì chúng trả lời hai câu hỏi khác nhau; picker của wizard dùng `hasFinalReport`.
        //
        // Vị ngữ so `!= null` khớp CHÍNH XÁC cách cột được chiếu ra `HasFinalReport` ở dưới —
        // lệch nhau thì cờ trả về và tập được lọc mâu thuẫn nhau. Lý do không thêm `&& != ""`:
        // xem chú thích tại chỗ chiếu.
        if (hasFinalReport == true)
            query = query.Where(x => x.FinalReport != null);
        else if (hasFinalReport == false)
            query = query.Where(x => x.FinalReport == null);

        // Keyset (CreatedAt DESC, Id DESC) — Id tie-break để hai roadmap trùng created_at vẫn có thứ
        // tự tổng, không lặp/sót dòng khi lật trang.
        if (cur is not null)
            query = query.Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0));

        // BE-6 — chiếu các cột CẦN xuống SQL rồi mới dựng response trong bộ nhớ. Không gọi
        // `RoadmapNaming.Resolve` thẳng trong `.Select` được: EF phải dịch cây biểu thức sang SQL và
        // sẽ hoặc ném, hoặc âm thầm kéo cả bảng về client để đánh giá. Vẫn KHÔNG có `Include` cây
        // milestone→lesson — xem chú thích trên `RoadmapSummaryResponse` về lý do list bỏ nó.
        var raw = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.JobCategory,
                x.Level,
                x.Mode,
                x.Language,
                x.CvId,
                x.Status,
                x.CreatedAt,
                x.CompletedAt,
                // Tính TRONG SQL, KHÔNG kéo cả `final_report` về: cột đó là jsonb chứa nguyên báo
                // cáo tổng kết, kéo về chỉ để kiểm rỗng là phình payload của một endpoint DANH SÁCH.
                //
                // ⚠ CỐ Ý chỉ so `!= null`, KHÔNG thêm `&& != ""`: cột là jsonb, so với chuỗi rỗng
                // trong SQL là chỗ Npgsql và SQLite hành xử khác nhau (SQLite lưu jsonb như TEXT nên
                // test vẫn xanh trong khi Postgres vỡ) — đúng lớp bug đã cắn repo nhiều lần. Không
                // cần vế đó: chỉ có HAI chỗ ghi cột này (RoadmapReportService đặt JSON khi hoàn tất,
                // RoadmapLessonService.RetryLessonAsync đặt `null` khi làm lại), không đường nào sinh
                // ra chuỗi rỗng — đo trên dev: 3/3 Completed non-null, 26/26 Active null, 0 rỗng.
                HasFinalReport = x.FinalReport != null
            })
            .ToListAsync(ct);

        var rows = raw
            .Select(x => new RoadmapSummaryResponse(
                x.Id,
                RoadmapNaming.Resolve(x.Name, x.JobCategory, x.Level, x.Language, x.CreatedAt),
                x.JobCategory.ToString(),
                x.Level.ToString(),
                x.Mode.ToString(),
                x.CvId,
                x.Status.ToString(),
                x.CreatedAt,
                x.CompletedAt,
                x.HasFinalReport))
            .ToList();

        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<RoadmapSummaryResponse>(rows, next);
    }

    // BC17 — trần độ dài mô tả tự do (rẻ, chống prompt phình). `SummaryMaxChars` (từng cắt bối cảnh
    // CV/roadmap trước gửi AI) đã GỠ cùng REC1-B7 — hai nguồn đó không còn được chưng cất/gửi đi.
    private const int FocusMaxChars = 2000;

    // MIS1-B6 — GUARD 1: trần số buổi làm nguồn cho 1 lộ trình. Không phải giá trị tuỳ ý — RoadmapMistakeLoader
    // đã tự ép trần 4 tiêu chí × 3 lỗi = 12 rồi (REC1-B6: theo scope), nhưng KHÔNG ép trần số buổi
    // ĐẦU VÀO của truy vấn (Distinct + IN-list) lẫn kích thước tập weaknesses/criteria đi kèm mỗi
    // buổi. 20 đủ rộng cho mọi wizard picker thực tế, đủ hẹp để chặn payload bất thường.
    private const int MaxSourceSessions = 20;

    // REC1-B2 mục A — sàn khi KHÔNG suy được mức nào từ buổi nguồn (phòng thủ; xem CreateAsync —
    // trên đường thật `chosen` luôn có ≥1 phần tử tới điểm dùng hằng số này). Junior khớp default
    // của chính `PracticeSession.Seniority`/`PracticeService.DefaultSeniority` — không bịa mốc mới.
    private const RoadmapLevel DefaultRoadmapLevel = RoadmapLevel.Junior;

    // 🔴 REC1-B7 — `BuildCvAnalysisSummary`/`BuildPriorRoadmapSummary` (dựng bối cảnh text từ CV/
    // roadmap trước cho prompt) đã XOÁ cùng `WebJson`/`Truncate`/`SummaryMaxChars` — cả năm chỉ
    // phục vụ đúng hai khối `CvAnalysisId`/`PriorRoadmapId` đã gỡ khỏi `CreateAsync` (xem comment
    // đầu hàm đó), không còn caller nào khác. `ReadOwnedParsedTextAsync` (bên dưới) CỐ Ý GIỮ LẠI dù
    // cũng mất caller duy nhất — xoá nó kéo theo xoá field `_storage`/tham số constructor, một thay
    // đổi RIPPLE sang 11 nơi dựng `RoadmapService` trực tiếp mà đề bài KHÔNG yêu cầu (phạm vi item 3
    // chỉ là lời gọi 2 dòng, không phải định nghĩa hàm).

    /// <summary>
    /// MIS1-B5 — lọc <paramref name="ids"/> (mistake_key AI tự gán) về đúng tập
    /// <paramref name="validKeys"/> (mistake_key ĐÃ CẤP thật cho lượt gọi này). Chống bịa
    /// BY-CONSTRUCTION (mẫu <c>RoadmapLessonService.NarrowToCited</c>) — CHÍNH .NET phải lọc,
    /// không tin nhãn AIService trả về dù phía Python đã có <c>filter_milestone_mistakes</c>: hai
    /// service deploy RỜI NHAU, một bản AIService cũ/lỗi ghi thẳng id treo xuống đây là ghi thẳng
    /// vào DB InterviewService.
    ///
    /// <c>null</c> (AI không trả field này — bản cũ, hoặc lượt không gửi mistakes) → giữ
    /// <c>null</c>. Có trả (kể cả rỗng SAU lọc) → danh sách đã lọc, KHÔNG BAO GIỜ drop milestone/
    /// lesson vì lý do này — quyết định "gom được lỗi nào chưa" là việc của AIService (MIS1-B2).
    /// </summary>
    private static List<string>? NarrowMistakeRefs(
        IReadOnlyList<string>? ids, HashSet<string> validKeys)
        => ids?.Where(validKeys.Contains).ToList();

    // Đọc parsed_text của file thuộc về candidate. null → 404; khác chủ → 403; rỗng → 400 (mẫu CvAnalysisService).
    private async Task<string> ReadOwnedParsedTextAsync(
        Guid fileId, Guid candidateId, string label, CancellationToken ct)
    {
        var file = await _storage.GetMetadata(fileId, ct)
            ?? throw new KeyNotFoundException($"{label} không tồn tại");

        if (file.UserId != candidateId)
            throw new UnauthorizedAccessException($"Không phải file {label} của bạn");

        if (string.IsNullOrWhiteSpace(file.ParsedText))
            throw new InvalidOperationException($"{label} không đọc được nội dung");

        return file.ParsedText;
    }

    // BK36 — chỉ `null` (client KHÔNG gửi field) mới rơi về mặc định "vi". Chuỗi rỗng là GIÁ TRỊ SAI,
    // phải bị từ chối chứ không được âm thầm nuốt thành "vi" — mẫu khớp PracticeService.ValidateLanguage.
    private string ValidateLanguage(string? requested)
    {
        if (requested is null) return "vi";
        var language = requested.Trim().ToLowerInvariant();
        if (language is not ("vi" or "en"))
            throw new InvalidOperationException("language chỉ nhận vi hoặc en.");
        if (!_bilingualEnabled && language != "vi")
            throw new InvalidOperationException("Bilingual interview chưa được bật.");
        return language;
    }

    /// <summary>
    /// Câu 400 dùng chung cho MỌI nguồn dữ liệu lệch nghề (buổi luyện · phân tích CV · lộ trình
    /// tham chiếu). Ba chỗ gọi chứ không phải một, vì người dùng bỏ chọn ba nguồn đó ở ba chỗ khác
    /// nhau trên wizard và câu lỗi phải chỉ đúng chỗ cần sửa (mẫu hai guard tách rời của chế độ
    /// <c>Reinforce</c>) — nhưng cách DIỄN ĐẠT chỉ được định nghĩa MỘT LẦN ở đây, vì đây là cùng
    /// một khái niệm và ba câu chữ trôi khỏi nhau sẽ khiến frontend phải nhận dạng ba dạng lỗi.
    ///
    /// <para><c>offending</c> LUÔN nêu nghề thật + id, nên chỉ được truyền vào những id ĐÃ qua cửa
    /// kiểm sở hữu — xem ghi chú tại từng chỗ gọi.</para>
    /// </summary>
    private static InvalidOperationException CrossCategorySource(
        string source, JobCategory wanted, string offending)
        => new($"{source} thuộc nghề khác với lộ trình đang tạo ({wanted}): {offending}. " +
               "Hãy bỏ chọn nguồn lệch nghề, hoặc đổi nghề của lộ trình cho khớp.");

    // MIS1-B6 — Guard NGÔN NGỮ, mẫu CrossCategorySource ngay trên (đứng riêng, không tái dùng
    // chung hàm: CrossCategorySource còn được 2 guard KHÁC không thuộc bước này gọi tới — cùng sửa
    // vào một hàm dùng chung là chạm cả những call site ngoài phạm vi bước này). Mang PREFIX mã lỗi
    // — ROADMAP_LANGUAGE_MISMATCH — vì đây là guard MỚI của MIS1-B6, khác CrossCategorySource cũ
    // chưa có mã (ngoài phạm vi bước này để thêm).
    private static InvalidOperationException LanguageMismatchSource(
        string source, string wanted, string offending)
        => new($"ROADMAP_LANGUAGE_MISMATCH: {source} thuộc ngôn ngữ khác với lộ trình đang tạo " +
               $"({wanted}): {offending}. Hãy bỏ chọn buổi lệch ngôn ngữ, hoặc đổi ngôn ngữ của " +
               "lộ trình cho khớp.");

    // BE-4 — độ dài roadmap candidate CHỌN. Tập đóng, case-sensitive (mẫu ValidateSeniority của
    // PracticeService) — chỉ `null` (client KHÔNG gửi field) mặc định "Standard"; chuỗi rỗng/giá
    // trị lạ là GIÁ TRỊ SAI, bị từ chối 400 chứ không âm thầm rơi về mặc định (BK36).
    private static readonly string[] AllowedScopes = ["Quick", "Standard"];
    private const string DefaultScope = "Standard";

    private static string ValidateScope(string? requested)
    {
        if (requested is null) return DefaultScope;
        var scope = requested.Trim();
        if (!AllowedScopes.Contains(scope, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"scope chỉ nhận {string.Join(" / ", AllowedScopes)} (đang gửi: '{requested}').");
        return scope;
    }

    /// <summary>
    /// Chế độ lộ trình candidate CHỌN. Tập đóng, case-sensitive — mẫu <see cref="ValidateScope"/>:
    /// chỉ <c>null</c> (client KHÔNG gửi field) mới mặc định <c>LevelUp</c>; chuỗi rỗng hoặc giá
    /// trị lạ là GIÁ TRỊ SAI và bị từ chối 400, KHÔNG âm thầm rơi về mặc định (BK36).
    ///
    /// ⚠ Nghiêm hơn phía AIService có chủ đích: <c>app.roadmap_mode.normalize_mode</c> fail-OPEN
    /// (giá trị lạ → LevelUp) vì ở đó một lỗi gõ chỉ nên làm mất tính năng chứ không nên thành
    /// 502. Chỗ TỪ CHỐI phải là đây — nơi biết đây là request của người dùng thật và trả lời
    /// được cho họ biết sai chỗ nào.
    /// </summary>
    private static RoadmapMode ValidateMode(string? requested)
    {
        if (requested is null) return RoadmapMode.LevelUp;
        // Enum.TryParse mặc định CHẤP NHẬN cả chuỗi số ("1" → Reinforce) lẫn sai hoa/thường —
        // cả hai đều là đầu vào ta không hứa hỗ trợ, nên so khớp tường minh thay vì dùng nó.
        var mode = requested.Trim();
        if (mode == nameof(RoadmapMode.LevelUp)) return RoadmapMode.LevelUp;
        if (mode == nameof(RoadmapMode.Reinforce)) return RoadmapMode.Reinforce;
        throw new InvalidOperationException(
            $"mode chỉ nhận {nameof(RoadmapMode.LevelUp)} / {nameof(RoadmapMode.Reinforce)} " +
            $"(đang gửi: '{requested}').");
    }

    /// <summary>
    /// Trạng thái lọc cho <c>GET /roadmaps</c>. Tập đóng, case-sensitive — cùng khuôn
    /// <see cref="ValidateMode"/> / <c>PracticeService.ValidateHistoryStatus</c>: <c>null</c>/rỗng =
    /// không lọc; giá trị lạ = GIÁ TRỊ SAI và bị từ chối 400, KHÔNG âm thầm bỏ qua filter.
    ///
    /// <para>Vì sao khớp chặt chứ không <c>Enum.TryParse</c>: nó nhận cả chuỗi số (<c>"1"</c> →
    /// <c>Completed</c>) lẫn sai hoa/thường — hai thứ ta không hứa hỗ trợ. Và vì sao không fail-open:
    /// endpoint này nuôi PICKER của wizard, nơi "không lọc được" KHÔNG vô hại — nó trả lại lộ trình
    /// chưa có báo cáo cho người dùng chọn, rồi <c>CreateAsync</c> từ chối bằng 400 sau khi họ đã chờ
    /// tạo roadmap. Cùng lập luận đã dùng cho <c>?status=</c> của <c>/practice/history</c>.</para>
    /// </summary>
    private static RoadmapStatus? ValidateRoadmapStatus(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var value = requested.Trim();
        foreach (var name in Enum.GetNames<RoadmapStatus>())
            if (string.Equals(value, name, StringComparison.Ordinal))
                return Enum.Parse<RoadmapStatus>(name);
        throw new InvalidOperationException(
            $"status chỉ nhận {string.Join(" / ", Enum.GetNames<RoadmapStatus>())} " +
            $"(đang gửi: '{requested}').");
    }

    private static readonly IReadOnlyDictionary<Guid, int> EmptyAttemptCounts =
        new Dictionary<Guid, int>();

    /// <summary>
    /// Số lần đã làm của MỌI bài trong 1 lộ trình — 1 truy vấn gộp, không N+1.
    /// Bài chưa từng làm không có dòng nào ⇒ vắng khỏi dictionary ⇒ <c>Map</c> đọc ra 0.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> LoadAttemptCountsAsync(
        Guid roadmapId, CancellationToken ct)
        => await _db.Set<RoadmapLessonAttempt>().AsNoTracking()
            .Where(a => a.Lesson.Milestone.RoadmapId == roadmapId)
            .GroupBy(a => a.LessonId)
            .Select(g => new { LessonId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LessonId, x => x.Count, ct);

    /// <summary>
    /// <paramref name="attemptCounts"/> = số lần đã làm, theo lessonId. THAM SỐ BẮT BUỘC (không có
    /// giá trị mặc định) là có chủ đích: một call site mới quên nạp sẽ phải TỰ QUYẾT truyền gì, thay
    /// vì âm thầm nhận 0 và nói dối FE là bài chưa từng được làm.
    /// </summary>
    private static RoadmapResponse Map(
        Roadmap r, bool includeTheory, IReadOnlyDictionary<Guid, int> attemptCounts,
        string? scope = null) => new(
        r.Id,
        RoadmapNaming.Resolve(r.Name, r.JobCategory, r.Level, r.Language, r.CreatedAt),
        r.JobCategory.ToString(),
        r.Level.ToString(),
        // Đọc THẲNG con dấu của lộ trình, không suy lại từ trạng thái hôm nay (mẫu `rubricSource`
        // của BC-8): người dùng phải thấy đúng chế độ mình đã chọn lúc tạo.
        r.Mode.ToString(),
        r.Language,
        r.CvId,
        r.Status.ToString(),
        r.Milestones.OrderBy(m => m.OrderNo).Select(m => new MilestoneResponse(
            m.Id,
            m.OrderNo,
            m.Title,
            m.FocusCriteria,
            m.Status.ToString(),
            m.Improvement is null
                ? null
                : m.Improvement.Select(kv => new MilestoneImprovementResponse(kv.Key, kv.Value)).ToList(),
            m.Lessons.OrderBy(l => l.OrderNo).Select(l => new LessonResponse(
                l.Id,
                l.OrderNo,
                l.Title,
                includeTheory ? l.TheoryContent : null,
                l.SessionId,
                l.Status.ToString(),
                // F15 — tài liệu đi CÙNG lý thuyết (sinh chung 1 lượt): view nào giấu theory thì
                // cũng giấu resources, tránh hiện "tài liệu" cho lesson chưa mở.
                includeTheory
                    ? l.Resources.Select(RoadmapLessonService.MapResource).ToList()
                    : [],
                // RAG grounding — CHỈ hiện citation khi lý thuyết ĐÃ sinh (grounding_refs lúc đó = tập AI thật
                // sự cite, narrow ở OpenLessonAsync). Chưa mở → null (chưa claim nguồn nào).
                includeTheory && l.TheoryContent != null
                    ? GroundingMapper.ToCitations(l.GroundingRefs)
                    : null,
                attemptCounts.TryGetValue(l.Id, out var attempts) ? attempts : 0,
                // Luật "được làm lại không" dùng CHUNG một hàm với đường chi tiết lesson — hai bản
                // sao lệch nhau nghĩa là FE hiện nút ở màn này mà không hiện ở màn kia.
                RoadmapLessonService.CanRetry(l.Status)
                // MIS1-B5 — CẤM tường minh: KHÔNG truyền `mistakes` ở đường GET /roadmaps/{id} này
                // (hợp đồng không hứa mistakeReview ở đây — chỉ OpenLessonAsync mới trả, xem
                // RoadmapLessonService.MapLesson). Để mặc định null.
                )).ToList(),
            // MIS1-B5 — đã NARROW ở lúc tạo (CreateAsync); đọc thẳng Count, không lọc lại.
            m.MistakeRefs?.Count ?? 0
        )).ToList(),
        r.CreatedAt,
        r.CompletedAt,
        // BE-4 — provenance: `sourceSessionIds`/`baseline` được GHI xuống DB từ BC12 nhưng trước đây
        // KHÔNG endpoint nào đọc lại — candidate chọn report trong wizard rồi sau khi tạo xong KHÔNG
        // CÒN cách nào xem lại đã dựa trên gì. `Scope` KHÔNG được lưu (không migration cho task này)
        // nên CHỈ có nghĩa NGAY LÚC TẠO (CreateAsync truyền `scope` từ request đang xử lý); đọc lại
        // roadmap CŨ (GetAsync) không biết scope lúc tạo → `null`, KHÔNG suy đoán từ số milestone/
        // lesson hiện có (mẫu BK23: null = không biết, đừng bịa "khác" từ "không biết").
        new RoadmapResolvedFromResponse(
            r.SourceSessionIds ?? [],
            r.Baseline is not null,
            scope));
}
