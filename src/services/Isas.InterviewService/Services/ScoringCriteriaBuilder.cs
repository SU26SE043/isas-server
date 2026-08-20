using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// E9 — dựng danh sách tiêu chí (kèm mức neo <c>levels</c> + <c>anchors</c>) cho message chấm.
///
/// <para><b>Nguồn mức thống nhất:</b> nếu criterion CÓ <c>rubric_levels</c> (khai báo) → dùng;
/// nếu KHÔNG (B2B <c>campaign_criteria</c> / B2C chưa seed levels) → sinh <b>dải mặc định</b>
/// ngay tại Interview (xem <see cref="DefaultBand"/>).</para>
///
/// <para>Nhờ vậy E9 (AI chọn mức khớp thay vì tự bịa thang) đúng cho <b>cả B2B &amp; B2C</b> mà
/// KHÔNG cần đụng Campaign/<c>suggest-criteria</c>. Anchor (câu mẫu) chỉ có khi rubric_levels
/// khai — dải mặc định không có anchor.</para>
///
/// Dùng chung ở <see cref="AnswerService"/> (publish khi upload) và
/// <see cref="StuckAnswerRepublisher"/> (re-publish khi kẹt) để 2 đường build message giống nhau.
///
/// <para><b>Ai truyền <see cref="DefaultBandStyle"/>?</b> Hai đường publish ở trên (cả hai đã có
/// <c>ScoringOptions</c> trong tay) — cờ PHẢI đọc ở CẢ HAI, y như bài học của
/// <c>Scoring:UseSampleAnswer</c>: gạt cờ mà một đường không nghe thì "đã bật" chỉ đúng một nửa, và
/// triệu chứng duy nhất là answer đi đường cứu hộ được chấm bằng thước khác answer đi đường thường.
/// <c>AdminRubricPreviewService</c> CỐ Ý không truyền: <c>RunAsync</c> chặn thẳng bộ tiêu chí có
/// tiêu chí dưới 2 mốc khai (chấm thử mà rơi về dải mặc định thì chỉ kiểm chứng chính dải mặc
/// định), nên nhánh dải mặc định KHÔNG với tới được đường chấm thử.</para>
/// </summary>
public static class ScoringCriteriaBuilder
{
    public static List<ScoringCriterionDto> Build(
        IEnumerable<RubricCriterion> criteria,
        DefaultBandStyle defaultBandStyle = DefaultBandStyle.EveryInteger)
        => criteria.Select(c => ToDto(c, defaultBandStyle)).ToList();

    private static ScoringCriterionDto ToDto(RubricCriterion c, DefaultBandStyle defaultBandStyle)
    {
        // Mức khai báo (nếu có): sắp theo score tăng dần.
        var declared = (c.Levels ?? [])
            .OrderBy(l => l.Score)
            .ToList();

        // ⚠ Tiêu chí CÓ khai mốc KHÔNG đi qua cờ: đường này giữ nguyên tuyệt đối hành vi cũ (mốc do
        // người soạn viết ra là thước đo THẬT — cờ ở đây chỉ nói về cái sàn dựng thay khi thiếu mốc).
        var levels = declared.Count > 0
            ? declared.Select(l => new ScoringLevelDto { Score = l.Score, Descriptor = l.Descriptor }).ToList()
            : DefaultBand(c.MaxScore, c.Language, defaultBandStyle);

        // Anchor chỉ đến từ rubric_levels khai (DB15: câu mẫu nằm ở cột jsonb example_answers của mức);
        // dải mặc định không có câu mẫu. OUTPUT giữ nguyên hợp đồng cũ: {Score, ExampleAnswer} sort theo score.
        var anchors = declared
            .SelectMany(l => (l.ExampleAnswers ?? new List<string>())
                .Select(ex => new ScoringAnchorDto { Score = l.Score, ExampleAnswer = ex }))
            .OrderBy(a => a.Score)
            .ToList();

        return new ScoringCriterionDto
        {
            CriterionId = c.Id,
            Name = c.Name,
            Description = c.Description,
            MaxScore = c.MaxScore,
            Weight = c.Weight,
            Levels = levels,
            Anchors = anchors.Count > 0 ? anchors : null
        };
    }

    /// <summary>
    /// Trần số mốc của dải <see cref="DefaultBandStyle.Descriptive"/> — KHÔNG phụ thuộc
    /// <c>maxScore</c>. Xem <see cref="DefaultBand"/>.
    /// </summary>
    public const int MaxDefaultBandLevels = 6;

    // Bậc chất lượng dùng làm descriptor cho dải Descriptive. THỨ TỰ = từ thấp tới cao, và số phần tử
    // PHẢI bằng nhau ở hai ngôn ngữ (mốc thứ i lấy nhãn thứ i, chỉ khác chữ).
    //
    // Vì sao là bậc chất lượng chứ không phải con số: descriptor tồn tại để model có CÁI NEO đối
    // chiếu — "câu này thuộc bậc nào" là câu hỏi trả lời được từ chính bản ghi, còn "câu này là 17
    // hay 18 trên 30" thì không. Cũng vì thế nhãn phải ĐỘC LẬP THANG: cùng một câu trả lời khá thì
    // rơi vào "Khá" dù rubric để thang 5 hay thang 30.
    //
    // Mỗi nhãn kèm một vế mô tả GENERIC (đủ ý / dẫn chứng / đánh đổi) — cố ý không nhắc tới nội dung
    // ngành nào, vì dải này dùng chung cho MỌI tiêu chí chưa khai mốc (giao tiếp, thuật ngữ, thiết kế
    // hệ thống…). Mốc THẬT của từng tiêu chí là việc của `rubric_levels`; đây chỉ là cái sàn.
    private static readonly string[] DescriptorsVi =
    [
        "Không đáp ứng — không trả lời được, lạc đề, hoặc sai bản chất vấn đề.",
        "Yếu — có chạm vào chủ đề nhưng thiếu phần lớn ý cốt lõi, không có dẫn chứng.",
        "Trung bình — nêu được ý cốt lõi ở mức cơ bản, còn thiếu chiều sâu và dẫn chứng cụ thể.",
        "Khá — đủ ý cốt lõi và có dẫn chứng, nhưng chưa đầy đủ hoặc chưa nói tới đánh đổi.",
        "Tốt — đủ ý và có chiều sâu, dẫn chứng cụ thể từ kinh nghiệm thật, có phân tích đánh đổi.",
        "Xuất sắc — đầy đủ, chính xác và sâu; dẫn chứng thuyết phục, nêu được cả đánh đổi lẫn trường hợp biên."
    ];

    private static readonly string[] DescriptorsEn =
    [
        "Not met — no answer, off-topic, or fundamentally wrong.",
        "Weak — touches the topic but misses most core points; no evidence given.",
        "Average — covers the core points at a basic level; lacks depth and concrete evidence.",
        "Good — covers the core points with evidence, but incomplete or no trade-offs discussed.",
        "Strong — complete and in depth, concrete evidence from real work, discusses trade-offs.",
        "Excellent — complete, accurate and deep; convincing evidence, covers trade-offs and edge cases."
    ];

    /// <summary>
    /// Dải mức MẶC ĐỊNH khi tiêu chí không khai <c>rubric_levels</c>.
    /// <list type="bullet">
    /// <item><see cref="DefaultBandStyle.EveryInteger"/> (mặc định) — mọi số nguyên
    /// <c>0..maxScore</c>, descriptor <c>"Mức i/maxScore"</c>. Hành vi có từ E9.</item>
    /// <item><see cref="DefaultBandStyle.Descriptive"/> (opt-in) — tối đa
    /// <see cref="MaxDefaultBandLevels"/> mốc trải đều trên <c>[0, maxScore]</c> (luôn gồm cả 0 lẫn
    /// <c>maxScore</c>), descriptor là bậc chất lượng so sánh được, song ngữ theo
    /// <paramref name="language"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para><b>Bối cảnh:</b> <c>select count(*) from rubric_levels</c> trên production = <b>0</b> —
    /// KHÔNG một tiêu chí nào khai mốc, tức 100% lượt chấm đi qua đúng hàm này. Ở dải cũ, bộ chấm
    /// được bảo "chọn mức khớp nhất" từ một danh sách số nguyên mà descriptor chỉ lặp lại chính con
    /// số đó (thang 30 ⇒ 31 dòng prompt, không dòng nào mang thông tin).</para>
    ///
    /// <para><b>🔴 ĐÃ NGHIỆM THU — GIẢ THUYẾT KHÔNG ĐỨNG VỮNG. GIỮ CỜ TẮT.</b> Dải này ra đời từ giả
    /// thuyết "mốc rỗng nghĩa làm chấm mất tái lập". Đã đo, và giả thuyết SAI: xem mục 1 ngay dưới.
    /// Code giữ lại vì nó vẫn đúng về mặt prompt (thang 30 ra 6 dòng thay vì 31) và vì phép đo có
    /// thể lặp lại khi có dữ liệu tốt hơn — nhưng KHÔNG có căn cứ để bật trên production.</para>
    /// <list type="number">
    /// <item><b>Tái lập — hoá ra KHÔNG kém, và dải mới KHÔNG cải thiện.</b> Chạy CÙNG cấu hình HAI
    /// LẦN rồi so hai lần với nhau (40 câu thật, temperature=0, trần thinking 512):
    /// <code>
    /// dải CŨ  (mọi số nguyên): 90,7% cặp cùng điểm · 9,3% đổi ≥1 mức · 4,7% đổi ≥2 · sd 0,636
    /// dải MỚI (có mô tả)     : 92,1% cặp cùng điểm · 7,9% đổi ≥1 mức · 3,1% đổi ≥2 · sd 0,638
    /// </code>
    /// Chênh 1,4 điểm phần trăm, trong khi sai số chuẩn của hiệu ≈ 2,7 ⇒ KHÔNG phân biệt được với
    /// không đổi. Tốc độ và token cũng gần như y hệt (9,0s → 8,7s; 1.516 → 1.498 token).
    /// <para>⚠ Con số <b>32,0%</b> từng được dẫn ở đây (và ở <c>ScoringOptions</c>,
    /// <c>benchmark-scoring.py</c>) là ARTEFACT ĐO, đừng dùng lại: nó so một lượt chấm MỚI (cây mã
    /// local) với điểm production ĐÃ LƯU — vốn do image <c>main-fb68e077…</c> chấm ở một thời điểm
    /// khác. Nó đo trôi-giữa-hai-phiên-bản cộng nhiễu, không phải tái lập. Muốn đo tái lập thì phải
    /// chạy CÙNG cấu hình hai lần như trên.</para></item>
    /// <item><b>Trôi nhẹ theo thang.</b> Cùng một câu trả lời, cùng 7 tiêu chí, cùng transcript, CHỈ
    /// đổi <c>maxScore</c>, chấm qua đúng <c>provider.score()</c> với dải cũ:
    /// <code>
    /// thang  5 → điểm thô TB  3,86 → 77,1%
    /// thang 10 → điểm thô TB  7,71 → 77,1%
    /// thang 20 → điểm thô TB 14,29 → 71,4%
    /// thang 30 → điểm thô TB 20,29 → 67,6%
    /// </code>
    /// Model CÓ scale theo thang — thang càng lớn chỉ càng dè dặt tương đối (77,1% → 67,6%), chứ
    /// KHÔNG sụp.</item>
    /// </list>
    ///
    /// <para><b>⚠ Đừng dựng lại chẩn đoán "thang lớn làm điểm sụp".</b> Nó từng được rút ra từ bảng
    /// <c>session_criterion_scores</c> gộp theo <c>max_score</c> (các ô thang lớn chỉ 4,0–4,7%) và đã
    /// bị thí nghiệm đối chứng ở trên BÁC BỎ. Bảng đó đọc sai theo hai đường: mỗi ô thang 12/15/16/18/
    /// 20/30 thực chất là MỘT tiêu chí của một rubric riêng (BE/vi, 9 tiêu chí mỗi cái một thang) xuất
    /// hiện ở 6 buổi — không phải một quần thể; và mức trung bình thấp trên toàn hệ chủ yếu do dữ
    /// liệu rác — 99 answer bị 0 điểm ở MỌI tiêu chí có transcript trung bình 116 ký tự (ngắn nhất 5),
    /// chấm 0 cho chúng là ĐÚNG chứ không phải lỗi thước đo.</para>
    ///
    /// <para><b>Muốn đo lại thì đo thế nào:</b> <c>scripts/benchmark-scoring.py --band descriptive
    /// --budgets=512,512</c> chạy HAI pass giống hệt nhau rồi so hai pass với nhau; chạy tiếp
    /// <c>--band every-integer</c> để có mốc đối chứng. So hai pass CÙNG cấu hình mới là đo tái lập —
    /// so với điểm production đã lưu thì lẫn cả trôi-giữa-hai-phiên-bản vào (bài học ở mục 1).
    /// Harness mirror thuật toán dưới đây bằng Python; lệch một mốc là phép đo vô nghĩa, nên có
    /// <c>DefaultBandTests</c> ghim sẵn bộ mốc của vài thang để đối chiếu chéo.</para>
    ///
    /// <para><b>Ba tính chất của dải Descriptive</b> (có test khoá từng cái, <c>DefaultBandTests</c>):</para>
    /// <list type="number">
    /// <item>Số mốc CÓ TRẦN (<see cref="MaxDefaultBandLevels"/>) và không phụ thuộc <c>maxScore</c> —
    /// thang 30 ra 6 dòng prompt thay vì 31.</item>
    /// <item>Mốc là số nguyên trong <c>[0, maxScore]</c>, KHÔNG trùng nhau, luôn gồm 0 và
    /// <c>maxScore</c>. <c>maxScore</c> nhỏ (1..4) ⇒ số mốc tự co lại còn <c>maxScore+1</c> (chính là
    /// mọi số nguyên) chứ không sinh mốc trùng.</item>
    /// <item>Descriptor độc lập thang — cùng bậc chất lượng cho mọi <c>maxScore</c>.</item>
    /// </list>
    ///
    /// <para><b>Mốc thưa KHÔNG làm mất điểm ở callback</b> — đã dò cả hai đầu trước khi sửa:
    /// worker Python tự suy <c>levels_by_id</c> từ đúng mảng này rồi SNAP điểm model trả về mức gần
    /// nhất và gửi <c>score = levelMatched</c>; còn <see cref="AnswerService"/><c>.ResolveLevel</c>
    /// ở nhánh dải-mặc-định GIỮ NGUYÊN điểm đã kẹp (chỉ nhánh CÓ khai <c>rubric_levels</c> mới snap
    /// cứng, tie-break về mức thấp hơn). Nghĩa là điểm về từ worker luôn nằm sẵn trong tập mốc, không
    /// có gì để snap hay drop. Ràng buộc còn lại nằm ở <see cref="ValidLevelScores"/> — đọc ghi chú ở đó.</para>
    /// </remarks>
    public static List<ScoringLevelDto> DefaultBand(
        int maxScore, string language = "vi",
        DefaultBandStyle style = DefaultBandStyle.EveryInteger)
    {
        var top = Math.Max(maxScore, 0);

        // Hành vi có từ E9 — MẶC ĐỊNH. Giữ nguyên từng byte để việc bật/tắt cờ là một phép đảo sạch.
        if (style == DefaultBandStyle.EveryInteger)
            return Enumerable.Range(0, top + 1)
                .Select(i => new ScoringLevelDto
                {
                    Score = i,
                    Descriptor = language == "en" ? $"Level {i}/{top}" : $"Mức {i}/{top}"
                })
                .ToList();

        var labels = language == "en" ? DescriptorsEn : DescriptorsVi;

        // top+1 = số số nguyên có trong thang. Lấy min với trần ⇒ thang nhỏ (2/3/4) tự co xuống đúng
        // số mốc khai được mà KHÔNG đẻ mốc trùng, thang lớn dừng ở trần.
        var count = Math.Min(MaxDefaultBandLevels, top + 1);

        // maxScore = 0 (dữ liệu méo) ⇒ thang chỉ có đúng một điểm. Trả 1 mốc thay vì chia cho 0.
        if (count <= 1)
            return [new ScoringLevelDto { Score = 0, Descriptor = labels[0] }];

        var levels = new List<ScoringLevelDto>(count);
        for (var i = 0; i < count; i++)
        {
            // Mốc trải đều: i/(count-1) của thang. i=0 ⇒ 0 và i=count-1 ⇒ top là ĐÚNG BẰNG phép
            // toán (không phải nhờ làm tròn), nên hai đầu thang luôn có mặt.
            // Không bao giờ rơi vào .5: 2·i·top = 10k+5 là vế chẵn bằng vế lẻ ⇒ vô nghiệm.
            var score = (int)Math.Round((double)i * top / (count - 1), MidpointRounding.AwayFromZero);

            // Nhãn cũng trải đều trên thang nhãn, để bộ mốc ngắn vẫn giữ hai đầu ("Không đáp ứng" ↔
            // "Xuất sắc") và lấy các bậc giữa cách đều. ToEven cố ý: count=3 cho nhãn giữa là
            // "Trung bình" (index 2) chứ không nhảy lên "Khá" (index 3).
            var label = labels[(int)Math.Round(
                (double)i * (labels.Length - 1) / (count - 1), MidpointRounding.ToEven)];

            levels.Add(new ScoringLevelDto { Score = score, Descriptor = label });
        }

        return levels;
    }

    /// <summary>
    /// Tập điểm mức HỢP LỆ của 1 tiêu chí (để C# guard snap/validate ở callback):
    /// điểm các <c>rubric_levels</c> khai nếu có; nếu không → đúng tập điểm của
    /// <see cref="DefaultBand"/> ở CÙNG <paramref name="style"/>.
    /// </summary>
    /// <remarks>
    /// <b>⚠ Hàm này và <see cref="DefaultBand"/> là HAI ĐẦU CỦA CÙNG MỘT HỢP ĐỒNG.</b>
    /// <see cref="Build"/> gửi <c>levels</c> sang worker Python (worker tự suy <c>levels_by_id</c> từ
    /// đó và snap điểm về tập ấy), còn hàm này là thứ phía C# dùng để kiểm điểm worker gửi về. Hai
    /// bên lệch nhau ⇒ điểm hợp lệ bị coi là "ngoài mức" rồi snap/drop ⇒ MẤT ĐIỂM IM LẶNG: không log,
    /// không exception, chỉ là điểm thấp hơn. Vì thế ở đây <b>gọi thẳng <see cref="DefaultBand"/></b>
    /// chứ không chép lại công thức — chép được thì sớm muộn sẽ lệch, và lệch ở đây không có triệu
    /// chứng. <c>language</c> không ảnh hưởng tập ĐIỂM (chỉ đổi chữ descriptor) nên truyền cố định.
    /// </remarks>
    public static IReadOnlyList<int> ValidLevelScores(
        IEnumerable<RubricLevel> declaredLevels, int maxScore,
        DefaultBandStyle style = DefaultBandStyle.EveryInteger)
    {
        var declared = declaredLevels
            .Select(l => l.Score)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        if (declared.Count > 0) return declared;

        return DefaultBand(maxScore, "vi", style).Select(l => l.Score).ToList();
    }
}
