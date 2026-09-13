using System.Security.Cryptography;

namespace Isas.CampaignService.Services;

/// <summary>Một câu trong ngân hàng đề, rút gọn cho việc chọn (không kéo cả entity vào).</summary>
public record PoolQuestion(Guid Id, string Text, string? SampleAnswer, bool IsRequired, string? Group)
{
    /// <summary>
    /// SC2 · W2 — nhãn tiêu chí NỘI DUNG câu này nhắm tới (<c>campaign_questions.target_criterion_ids</c>).
    /// <c>null</c>/rỗng = câu chưa gắn nhãn ⇒ rơi về nhóm theo <see cref="Group"/> như trước SC2 (I5).
    /// Phần tử ĐẦU = <b>tiêu chí chính</b> — khoá rổ khi chia đều (xem <see cref="QuestionPoolSelector"/>).
    /// Khai init-only (không positional) để mọi chỗ dựng <c>new PoolQuestion(a,b,c,d,e)</c> cũ vẫn biên dịch.
    /// </summary>
    public IReadOnlyList<Guid>? TargetCriterionIds { get; init; }
}

/// <summary>
/// NGÂN HÀNG ĐỀ — chọn bộ câu hỏi cho MỘT ứng viên từ bộ câu hỏi của chiến dịch.
///
/// <para>Trước tính năng này, <c>ParticipationService</c> gửi TRỌN bộ câu hỏi sang Interview và
/// <c>PracticeService</c> cũng lấy trọn làm đề — tức HR up 60 câu là ứng viên phải trả lời đủ 60 câu.
/// Trần <c>MaxQuestions</c> không cứu được: nó chỉ giới hạn số câu ĐÀO SÂU do AI sinh thêm.</para>
///
/// <para><b>Thuần, không DbContext, không I/O</b> — để test được mà không cần SQLite, và để nếu ai đó
/// sau này lỡ tay thêm truy vấn vào giữa thuật toán chọn thì nó lộ ra ngay ở chữ ký.</para>
///
/// <para><b>Vì sao rút ĐỀU THEO NHÓM chứ không rút mù:</b> luật chấm-theo-phạm-vi (INT-18) LOẠI tiêu chí
/// không câu nào hỏi tới ra khỏi điểm, không tính 0. Rút mù thì ứng viên A bốc 4 câu thuật toán bị chấm
/// gắt ở mảng đó, còn B bốc 0 câu thì mảng đó BIẾN MẤT khỏi điểm của B — rồi hai người xếp chung một
/// bảng (CAMP-10). Đó là đo bằng hai thước khác nhau, không phải "đề khác nhau một chút".</para>
///
/// <para><b>SC2 · W2 — rổ chia = TIÊU CHÍ CHÍNH của câu</b> (<c>TargetCriterionIds[0]</c>), rơi về
/// <c>question_group</c> khi câu chưa gắn nhãn. Nhãn là thứ INT-18 dùng để quyết định tiêu chí nào được
/// chấm, nên chia đều theo nhãn mới thật sự bảo đảm "mỗi ứng viên được hỏi đủ các tiêu chí" — chia theo
/// tên nhóm HR gõ chỉ là xấp xỉ. Rổ theo Guid và rổ theo tên nhóm là hai không gian khoá khác nhau nên
/// KHÔNG trộn (chấp nhận: chiến dịch nửa gắn nhãn nửa không sẽ có rổ "Guid" cạnh rổ "tên").</para>
/// </summary>
public static class QuestionPoolSelector
{
    /// <summary>
    /// Chọn đề cho một ứng viên.
    /// </summary>
    /// <param name="pool">Bộ câu hỏi của chiến dịch, ĐÃ sắp theo thứ tự HR soạn.</param>
    /// <param name="questionsPerSession">
    /// Số câu mỗi buổi. <c>null</c> = lấy hết theo đúng thứ tự HR soạn (hành vi trước tính năng này —
    /// chiến dịch cũ không đổi gì, không cần backfill dữ liệu).
    /// </param>
    /// <param name="campaignId">Cùng với <paramref name="candidateId"/> tạo hạt giống ngẫu nhiên.</param>
    /// <param name="candidateId">
    /// Rút phải TÁI LẬP ĐƯỢC theo cặp (chiến dịch, ứng viên), không dùng <c>Random()</c> trần: buổi thi
    /// là create-or-get, ứng viên đóng tab mở lại phải nhận ĐÚNG đề cũ; và khi có khiếu nại thì phải
    /// dựng lại được đề của một người cụ thể thay vì "đề đã bốc hơi".
    /// </param>
    /// <param name="onWarning">
    /// Nơi báo ca bất thường (số câu bắt buộc vượt trần). Không ném: ứng viên đang đứng ở màn bắt đầu
    /// và tổ chức đã bị giữ credit — nhưng cũng không cắt im lặng (tiền lệ F9).
    /// </param>
    public static List<PoolQuestion> Select(
        IReadOnlyList<PoolQuestion> pool,
        int? questionsPerSession,
        Guid campaignId,
        Guid candidateId,
        Action<string>? onWarning = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        // Không bật ngân hàng đề, hoặc bộ câu hỏi vốn đã nhỏ hơn số cần rút → thi trọn bộ, giữ nguyên
        // thứ tự HR soạn. Trả về danh sách mới để caller không vô tình sửa vào bộ gốc.
        if (questionsPerSession is not int take || take >= pool.Count)
            return pool.ToList();

        var rng = CreateRng(campaignId, candidateId);

        var required = pool.Where(q => q.IsRequired).ToList();
        var optional = pool.Where(q => !q.IsRequired).ToList();

        // Câu bắt buộc nhiều hơn cả trần: giữ hết (chúng bắt buộc vì HR nói thế), báo cho HR biết buổi
        // thi sẽ dài hơn con số họ đặt. Cắt bớt câu bắt buộc mới là thứ phản bội đúng chữ "bắt buộc".
        if (required.Count >= take)
        {
            if (required.Count > take)
                onWarning?.Invoke(
                    $"Chiến dịch có {required.Count} câu bắt buộc, nhiều hơn số câu mỗi buổi ({take}) — "
                    + "ứng viên sẽ nhận đủ số câu bắt buộc.");
            return Shuffle(required, rng);
        }

        var selected = new List<PoolQuestion>(required);
        var slots = take - required.Count;

        // Chia khe theo nhóm. Nhóm null gom về một nhóm mặc định — chiến dịch chưa phân nhóm thì mọi câu
        // rơi vào đây, và phép chia bên dưới suy biến về "rút ngẫu nhiên từ một rổ", đúng như mong đợi.
        // SC2: khoá rổ = tiêu chí chính (nhãn [0]) nếu có, không thì tên nhóm — xem BucketKey.
        var groups = optional
            .GroupBy(BucketKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)   // deterministic, không phụ thuộc thứ tự nạp
            .Select(g => new GroupBucket(g.Key, g.ToList()))
            .ToList();

        if (groups.Count == 0)
            return Shuffle(selected, rng);

        // BUG-2 (D-5 mở rộng) — RỔ TIÊU CHÍ đi trước, chia đều sau. Trước bản này, khi khe < số rổ, phần dư
        // rải cho các rổ ĐẦU theo thứ tự tên khoá — mà "" (không nhãn) và tên nhóm HR luôn đứng trước GUID ⇒
        // rổ không nhãn thắng, rổ tiêu chí bị bỏ theo thứ tự tên, và vì thứ tự rổ không phụ thuộc ứng viên,
        // một tiêu chí CHẾT với MỌI ứng viên cả chiến dịch (đo trên dev: K=2, required [C], optional {[],
        // [B], [C]} ⇒ B không bao giờ được hỏi) trong khi K-rule lẫn coverageWarnings đều im.
        //
        // Bước 1: mỗi rổ TIÊU CHÍ (khoá GUID = câu có nhãn) CHƯA được câu bắt buộc phủ (nhãn[0] của câu
        //         required) nhận 1 khe trước, theo thứ tự tên khoá, khi còn khe. Rổ ""/nhóm HR KHÔNG được
        //         ưu tiên — nó chỉ được phần chia đều ở bước 2.
        // Bước 2: phần khe còn lại rót "đầy dần" (water-fill) trên MỌI rổ: mỗi lần cho rổ đang có quota
        //         NHỎ NHẤT còn dư câu (hoà ⇒ thứ tự tên khoá). Khi khe ≥ số rổ, kết quả TRÙNG phép chia đều
        //         cũ (base + dư cho rổ đầu); khi rổ ít câu hơn phần được chia thì khe tự chảy sang rổ còn
        //         dư câu — thay cho vòng "leftover" cũ. Vẫn deterministic, không random phần chia.
        var coveredByRequired = required
            .Where(q => q.TargetCriterionIds is { Count: > 0 })
            .Select(BucketKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var quota = new int[groups.Count];
        var slotsLeft = slots;
        for (var i = 0; i < groups.Count && slotsLeft > 0; i++)
        {
            var isCriterionBucket = groups[i].Items.Any(q => q.TargetCriterionIds is { Count: > 0 });
            if (!isCriterionBucket || coveredByRequired.Contains(groups[i].Key)) continue;
            quota[i] = 1;
            slotsLeft--;
        }

        while (slotsLeft > 0)
        {
            var pick = -1;
            for (var i = 0; i < groups.Count; i++)
            {
                if (quota[i] >= groups[i].Items.Count) continue;   // rổ đã cạn câu
                if (pick < 0 || quota[i] < quota[pick]) pick = i;   // nhỏ nhất; hoà ⇒ rổ đứng trước (tên khoá)
            }
            if (pick < 0) break;   // hết câu để rót ở mọi rổ → chấp nhận thiếu, không lặp vô hạn
            quota[pick]++;
            slotsLeft--;
        }

        for (var i = 0; i < groups.Count; i++)
            selected.AddRange(Shuffle(groups[i].Items, rng).Take(quota[i]));

        // Xáo lần cuối: nếu không, câu bắt buộc luôn đứng đầu và các nhóm luôn theo đúng thứ tự tên —
        // ứng viên thi sau đoán được cấu trúc đề dù không biết câu cụ thể.
        return Shuffle(selected, rng);
    }

    private sealed record GroupBucket(string Key, List<PoolQuestion> Items);

    /// <summary>
    /// SC2 · W2 — khoá rổ của một câu: <b>tiêu chí chính</b> = phần tử ĐẦU của nhãn (không phải cuối,
    /// không phải mọi phần tử — một câu chỉ được đếm vào MỘT rổ, nếu không phép chia đều mất nghĩa);
    /// câu chưa gắn nhãn (<c>null</c> hoặc <c>[]</c>) ⇒ tên nhóm HR đặt, <c>null</c> ⇒ <c>""</c> (I5: chiến
    /// dịch cũ không đổi). Guid in dạng "D" để so được với khoá chuỗi bằng cùng comparer.
    /// </summary>
    internal static string BucketKey(PoolQuestion q)
        => q.TargetCriterionIds is { Count: > 0 } ids
            ? ids[0].ToString("D")
            : q.Group ?? string.Empty;

    /// <summary>
    /// Hạt giống = SHA-256 của hai Guid. Dùng băm chứ không XOR/cộng hai <c>GetHashCode()</c>: hash code
    /// của Guid không ổn định giữa các tiến trình (randomized hashing), nên "cùng ứng viên ra cùng đề"
    /// sẽ đúng trong một tiến trình rồi sai sau lần khởi động lại — kiểu hỏng chỉ lộ ra trên production.
    /// </summary>
    private static Random CreateRng(Guid campaignId, Guid candidateId)
    {
        Span<byte> buffer = stackalloc byte[32];
        campaignId.TryWriteBytes(buffer[..16]);
        candidateId.TryWriteBytes(buffer[16..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        return new Random(BitConverter.ToInt32(hash[..4]));
    }

    // Fisher-Yates trên bản sao — không sửa danh sách của caller.
    private static List<PoolQuestion> Shuffle(IEnumerable<PoolQuestion> items, Random rng)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
