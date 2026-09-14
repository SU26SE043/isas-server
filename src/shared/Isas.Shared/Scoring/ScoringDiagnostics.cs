namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · HĐ-2 — bảng MÃ lỗi của ngôn ngữ biểu thức chấm điểm. Chỉ có MÃ, KHÔNG có câu chữ:
/// FE map mã sang i18n vi/en, còn <see cref="ScoringError.Start"/>/<see cref="ScoringError.End"/>
/// là vị trí ký tự để tô đỏ đúng đoạn trong ô soạn thảo.
///
/// <para><b>Danh sách này ĐÓNG.</b> Thêm mã mới ⇒ FE chưa biết dịch ⇒ người dùng thấy một chuỗi
/// khoá thô. Nếu buộc phải thêm thì phải báo FE cùng lúc.</para>
/// </summary>
public static class ScoringErrorCodes
{
    // ── Lỗi tĩnh (phát hiện lúc PHÂN TÍCH, không cần dữ liệu ứng viên) ──────────────────────────
    public const string SyntaxError = "SYNTAX_ERROR";
    public const string UnknownFunction = "UNKNOWN_FUNCTION";
    public const string WrongArgCount = "WRONG_ARG_COUNT";
    public const string TooLong = "TOO_LONG";
    public const string TooDeep = "TOO_DEEP";
    public const string TooManyNodes = "TOO_MANY_NODES";

    // ── Lỗi lúc CHẠY (cần một ScoringContext — biến của một ứng viên hoặc bộ mẫu) ───────────────
    public const string UnknownVariable = "UNKNOWN_VARIABLE";
    public const string DivideByZero = "DIVIDE_BY_ZERO";
    public const string ResultOutOfRange = "RESULT_OUT_OF_RANGE";
}

/// <summary>
/// Một lỗi biểu thức: MÃ (<see cref="ScoringErrorCodes"/>) + khoảng ký tự nửa mở
/// <c>[Start, End)</c> trong chuỗi biểu thức gốc (<c>End - Start</c> = độ dài đoạn).
///
/// <para>Với lỗi cấu trúc toàn cục (<c>TOO_LONG</c>/<c>TOO_DEEP</c>/<c>TOO_MANY_NODES</c>) khoảng
/// tính từ ĐIỂM vi phạm tới hết biểu thức — đủ để FE cuộn tới chỗ cần cắt, không cố chỉ vào một
/// token cụ thể vì lỗi là của cả cây.</para>
/// </summary>
public sealed record ScoringError(string Code, int Start, int End);

/// <summary>
/// SCP1 · HĐ-1 — TRẦN CỨNG của biểu thức. Đây là lá chắn DoS: ngôn ngữ không có vòng lặp/đệ quy
/// nên không chạy vô hạn được, nhưng một biểu thức 50 KB hay lồng 5000 tầng vẫn đủ để đốt CPU/stack
/// khi <c>preview</c> chạy nó trên toàn bộ ứng viên. Các trần rộng gấp nhiều lần công thức thật
/// (xem seed template) nên không chặn nhầm.
/// </summary>
public static class ScoringLimits
{
    /// <summary>Độ dài chuỗi biểu thức (ký tự). Vượt → <c>TOO_LONG</c>. Công thức thật dài nhất
    /// trong seed &lt; 90 ký tự.</summary>
    public const int MaxExpressionLength = 1000;

    /// <summary>Tổng số node trong cây cú pháp. Vượt → <c>TOO_MANY_NODES</c>.</summary>
    public const int MaxNodeCount = 200;

    /// <summary>Độ sâu lồng của cây (= độ sâu đệ quy của bộ phân tích, tính cả ngoặc đơn). Vượt →
    /// <c>TOO_DEEP</c>. Chốt luôn nguy cơ tràn stack từ <c>((((…))))</c>.</summary>
    public const int MaxDepth = 32;

    /// <summary>Số tham số tối đa cho MỘT lời gọi hàm biến thiên (<c>min/max/avg/sum</c>). Vượt →
    /// <c>WRONG_ARG_COUNT</c> (HĐ-2 không có mã riêng cho "quá nhiều tham số"; đây là mã sát nghĩa nhất
    /// và cùng chỗ FE hiển thị với các lỗi arity khác).</summary>
    public const int MaxCallArguments = 16;
}
