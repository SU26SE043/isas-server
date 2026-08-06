namespace Isas.CampaignService.Services
{
    /// <summary>Quy đổi mốc UTC sang giờ Việt Nam để IN CHO NGƯỜI ĐỌC (không dùng để tính toán/lưu).</summary>
    public static class VietnamTime
    {
        // Offset CỐ ĐỊNH +07:00, KHÔNG dùng `TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")`:
        // Việt Nam không có DST nên +07:00 chính xác tuyệt đối, còn tra theo tên buộc image phải có gói
        // `tzdata` — thiếu thì ném LÚC CHẠY và chỉ trên nhánh này, tức không test nào bắt được.
        private static readonly TimeSpan Offset = TimeSpan.FromHours(7);

        /// <summary>
        /// Nhận mốc UTC (mọi <see cref="DateTimeKind"/>) → <see cref="DateTimeOffset"/> ở +07:00.
        /// </summary>
        /// <remarks>
        /// <para><c>SpecifyKind</c> ở đây là <b>phòng thủ, không phải vá một lỗi đang sống</b> — đã đo
        /// chứ không suy đoán: Npgsql trả <c>Kind = Utc</c>, còn SQLite (test) trả <c>Unspecified</c>;
        /// cả hai đều KHÔNG ném. Nghĩa là <c>Kind == Local</c> không tới được hàm này qua đường đọc DB.</para>
        /// <para>Vẫn giữ vì constructor <c>DateTimeOffset(DateTime, TimeSpan)</c> <b>ném
        /// <c>ArgumentException</c></b> khi <c>Kind == Local</c>, mà caller gần nhất
        /// (<c>ParticipationService.StartInterviewAsync</c>) nằm dưới một controller KHÔNG có
        /// <c>catch(Exception)</c> bao ngoài ⇒ sẽ thành <b>500 trên chính nhánh sinh ra để báo lỗi thân
        /// thiện</b>. Một lời gọi đổi lấy việc hàm đúng với mọi đầu vào thay vì chỉ đúng với thứ provider
        /// hiện tại tình cờ trả về. Repo đã dính đúng lớp lỗi này một lần (PR #80).</para>
        /// </remarks>
        public static DateTimeOffset From(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero).ToOffset(Offset);
    }
}
