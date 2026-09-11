using System.Collections.Immutable;

namespace Isas.Shared.Scoring;

/// <summary>Hai loại chính sách chấm (HĐ-1). Quyết định TẬP biến hợp lệ và chuỗi cho
/// <c>count_below</c>.</summary>
public enum ScoringExpressionKind
{
    Interview = 0,
    CvScreening = 1,
}

/// <summary>
/// SCP1 · HĐ-1 — TẬP TÊN BIẾN đã công bố cho mỗi loại chính sách.
///
/// <para>🔴 <b>APPEND-ONLY.</b> Một tên đã có ở đây thì KHÔNG BAO GIỜ được đổi nghĩa, và KHÔNG BAO GIỜ
/// được gỡ. Cần một cách tính khác ⇒ thêm TÊN MỚI (vd <c>weighted_avg_pct_strict</c>). Lý do: biểu thức
/// của mọi campaign lịch sử tham chiếu tên theo nghĩa lúc họ viết; đổi nghĩa tên cũ làm điểm của các
/// buổi thi đã đóng ÂM THẦM thay đổi ở lần <c>apply</c>/tính lại kế tiếp.</para>
///
/// <para>Nghĩa ĐEN của từng biến (cách suy từ dữ liệu thô) nằm ở <see cref="ScoringContext"/>. Ở đây
/// chỉ là danh sách tên + thứ tự ổn định để đưa vào vân tay (HĐ-4).</para>
/// </summary>
public static class ScoringVariableCatalog
{
    /// <summary>Biến cho chính sách PHỎNG VẤN. Thứ tự = thứ tự công bố, giữ nguyên (vào fingerprint).</summary>
    public static readonly ImmutableArray<string> Interview =
    [
        "weighted_avg_pct",
        "avg_pct",
        "min_pct",
        "max_pct",
        // ⚠ NGOẠI LỆ append-only, tường minh (2 lần, cùng một lý do):
        //   • SCP1/B12: `answered`/`completeness` đổi nghĩa TẠI CHỖ (đếm "có ghi âm" thay vì mọi hàng) —
        //     an toàn vì biến chưa từng ra giá trị khác 1 và prod chưa có scoring_inputs.
        //   • CAMP-21 (2026-09-11): `answered`/`completeness` VÀ `seed_answered`/`seed_completeness` cùng
        //     loại thêm bài VAD xác nhận IM LẶNG (`reject_reason = 'no_speech'`). Cân nhắc thêm
        //     `seed_answered_strict`: tên mới chỉ bảo vệ chiến dịch TỰ SOẠN biểu thức — mặc định không ai
        //     soạn — nên kẽ hở "+24,7 điểm nhờ nộp im lặng" vẫn mở cho toàn bộ chiến dịch đang chạy.
        //     Không hồi tố: dòng cũ `reject_reason NULL` vẫn tính, snapshot cũ đã đóng băng.
        "answered",
        "total_questions",
        "completeness",
        // RNK1 · HĐ-1 — APPEND (không đổi/không gỡ phần tử trên). `seed_*` chỉ đặt trong ScoringContext
        // khi buổi mang dữ liệu câu GỐC (seed); snapshot trước RNK1 thiếu ⇒ biến không tồn tại ⇒ biểu
        // thức tham chiếu → UNKNOWN_VARIABLE → lùi an toàn (giống mọi biến chưa có context).
        "seed_answered",
        "seed_total",
        "seed_completeness",
    ];

    /// <summary>Biến cho chính sách SÀNG CV.</summary>
    public static readonly ImmutableArray<string> CvScreening =
    [
        "strong_count",
        "partial_count",
        "weak_count",
        "need_count",
        "must_have_total",
        "must_have_met",
    ];

    public static ImmutableArray<string> For(ScoringExpressionKind kind) => kind switch
    {
        ScoringExpressionKind.Interview => Interview,
        ScoringExpressionKind.CvScreening => CvScreening,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Tên hàm hợp lệ (HĐ-1). Dùng chung cho cả hai loại. Cũng append-only.</summary>
    public static readonly ImmutableArray<string> Functions =
    [
        "min", "max", "avg", "sum", "round", "clamp", "if", "count_below",
    ];
}
