using System.Security.Cryptography;

namespace Isas.CampaignService.Services;

/// <summary>Một câu trong ngân hàng đề, rút gọn cho việc chọn (không kéo cả entity vào).</summary>
public record PoolQuestion(Guid Id, string Text, string? SampleAnswer, bool IsRequired, string? Group);

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
        var groups = optional
            .GroupBy(q => q.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)   // deterministic, không phụ thuộc thứ tự nạp
            .Select(g => new GroupBucket(g.Key, g.ToList()))
            .ToList();

        if (groups.Count == 0)
            return Shuffle(selected, rng);

        // Chia đều; phần dư rải cho các nhóm ĐẦU theo thứ tự tên (deterministic — không random phần chia,
        // vì chỗ ngẫu nhiên duy nhất nên là "chọn câu nào", không phải "nhóm nào được ưu ái").
        var quota = new int[groups.Count];
        var baseQuota = slots / groups.Count;
        var remainder = slots % groups.Count;
        for (var i = 0; i < groups.Count; i++)
            quota[i] = baseQuota + (i < remainder ? 1 : 0);

        // Nhóm nào ít câu hơn phần được chia thì lấy hết, khe thừa CHUYỂN sang nhóm còn dư câu. Không có
        // vòng chuyển này thì buổi thi ra thiếu câu so với con số HR đặt mà chẳng có lỗi nào.
        var leftover = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var available = groups[i].Items.Count;
            if (quota[i] > available)
            {
                leftover += quota[i] - available;
                quota[i] = available;
            }
        }
        while (leftover > 0)
        {
            var moved = false;
            for (var i = 0; i < groups.Count && leftover > 0; i++)
            {
                if (quota[i] >= groups[i].Items.Count) continue;
                quota[i]++;
                leftover--;
                moved = true;
            }
            if (!moved) break;   // hết câu để bù ở mọi nhóm → chấp nhận thiếu, không lặp vô hạn
        }

        for (var i = 0; i < groups.Count; i++)
            selected.AddRange(Shuffle(groups[i].Items, rng).Take(quota[i]));

        // Xáo lần cuối: nếu không, câu bắt buộc luôn đứng đầu và các nhóm luôn theo đúng thứ tự tên —
        // ứng viên thi sau đoán được cấu trúc đề dù không biết câu cụ thể.
        return Shuffle(selected, rng);
    }

    private sealed record GroupBucket(string Key, List<PoolQuestion> Items);

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
