namespace Isas.CampaignService.Services
{
    /// <summary>
    /// ATT1 — PUT /campaign đổi một tham số LUẬT THI mà trạng thái chiến dịch không cho đổi theo chiều đó.
    /// Controller trả <b>409</b> body <c>{ code, error }</c> — FE phân nhánh theo <c>code</c>, không đoán
    /// từ câu chữ:
    /// <list type="bullet">
    /// <item><c>MAX_ATTEMPTS_DECREASE</c> — Active mà GIẢM số lượt: người đang được hứa làm lại sẽ bị
    ///   chặn ngược. Chỉ TĂNG được.</item>
    /// <item><c>TIME_LIMIT_LOCKED</c> — đổi thời lượng khi không còn Draft: thời lượng là giờ server đóng
    ///   bài, đổi giữa chừng thì hai ứng viên cùng chiến dịch thi hai luật khác nhau.</item>
    /// </list>
    ///
    /// <para>KHÔNG dẫn xuất <see cref="System.InvalidOperationException"/> (controller map loại đó →
    /// 409 <c>{ error }</c> không có code). Mẫu <see cref="AdaptiveBudgetTooSmallException"/>: action bắt
    /// riêng loại này TRƯỚC catch generic. Chỉ ném khi giá trị THỰC SỰ ĐỔI — wizard luôn echo cả form
    /// (bài học CMP4-B2 <c>cd15cd5</c>).</para>
    /// </summary>
    public sealed class CampaignSettingLockedException : Exception
    {
        public const string MaxAttemptsDecreaseCode = "MAX_ATTEMPTS_DECREASE";
        public const string TimeLimitLockedCode = "TIME_LIMIT_LOCKED";

        private CampaignSettingLockedException(string code, string message) : base(message)
        {
            Code = code;
            Body = new { code, error = message };
        }

        public string Code { get; }

        /// <summary>Body 409 — khoá JSON camelCase khớp hợp đồng ATT1 [C2]/[C3]: <c>code · error</c>.</summary>
        public object Body { get; }

        public static CampaignSettingLockedException MaxAttemptsDecrease(int current, int requested) => new(
            MaxAttemptsDecreaseCode,
            $"Chiến dịch đang chạy chỉ được TĂNG số lượt làm bài (hiện {current}, gửi {requested}). "
            + "Giảm lúc này sẽ chặn ngược ứng viên đã được hứa làm lại.");

        public static CampaignSettingLockedException TimeLimitLocked(string status, int? current, int requested) => new(
            TimeLimitLockedCode,
            $"Thời lượng bài thi đã khoá khi chiến dịch {status} (hiện {(current?.ToString() ?? "chưa đặt")} phút, "
            + $"gửi {requested} phút). Chỉ đổi được khi chiến dịch còn Draft.");
    }
}
