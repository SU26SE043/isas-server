namespace Isas.CampaignService.Models
{
    /// <summary>
    /// MON1-B2 — cấu hình <c>MonitoringGapSweeper</c>: phép đo ĐỘC LẬP phía server về việc giám sát
    /// khuôn mặt có bị ĐỨT giữa buổi thi hay không.
    ///
    /// Hôm nay mọi cờ chống gian lận đều do máy ứng viên tự khai (đi qua <c>createCampaignFlag</c> từ
    /// trình duyệt). Ứng viên chặn 1 endpoint hoặc đóng tab ⇒ màn hình HR sạch bong, không phân biệt
    /// được "sạch vì trung thực" với "sạch vì bị bịt mắt". Mỗi lượt kiểm mặt ĐẾN NƠI để lại 1 dòng
    /// <see cref="FaceImage"/> kind=Live do SERVER ghi kèm <c>captured_at</c> — client ngừng gửi thì
    /// mốc đó ngừng tiến, và client KHÔNG can thiệp được vào dòng đó. Sweeper đọc chính nó.
    ///
    /// ⚠ Chỉ xét khoảng GIỮA hai lượt kiểm (cặp liên tiếp). Im lặng ở ĐUÔI buổi thì mơ hồ
    /// (buổi xong là kiểm mặt dừng, mà <c>interview_status</c> chỉ lật Completed khi event về qua
    /// RabbitMQ ⇒ có độ trễ) — gắn cờ ở đó là gắn oan người vừa nộp bài xong.
    /// </summary>
    public class MonitoringGapSettings
    {
        public const string SectionName = "MonitoringGap";

        /// <summary>
        /// CHẾ ĐỘ BÓNG. <c>false</c> (mặc định) = sweeper TÍNH và LOG đầy đủ khoảng trống phát hiện
        /// được nhưng KHÔNG ghi <c>session_flags</c>. Bật khi đã quan sát log một chu kỳ và tin ngưỡng
        /// (mẫu 3 job purge S8 P1 / <see cref="FaceImageRetentionSettings"/>).
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Khoảng cách (giây) giữa 2 lượt kiểm mặt liên tiếp mà VƯỢT thì coi là một khoảng trống
        /// giám sát. ĐIỂM KHỞI ĐẦU — sẽ hiệu chuẩn ở B5. Vì sao 90: nhịp kiểm 30s, sau bất thường
        /// 10s, cộng defer lúc FE đang upload câu trả lời (đo được 7–21s) ⇒ 3× nhịp là biên an toàn
        /// để không gắn cờ oan lúc hệ thống chỉ đang chậm.
        /// </summary>
        public int GapThresholdSeconds { get; set; } = 90;

        /// <summary>Nhịp quét (giây). Mặc định 120 — như <c>StuckScreeningRepublisher</c>.</summary>
        public int ScanIntervalSeconds { get; set; } = 120;

        /// <summary>
        /// Chỉ xét ảnh Live có <c>captured_at</c> trong ngần này giờ trở lại. Cận trên cho vòng quét:
        /// <c>face_images</c> tích tới <c>FaceImageRetention:RetentionDays</c> (90) ngày trước khi bị
        /// dọn, nên không có cận này thì mỗi vòng (2 phút) phải nạp + hậu-kiểm-chống-trùng trên
        /// <i>toàn bộ</i> lịch sử. Khoảng trống cũ hơn cửa sổ này HR đã xem hoặc không còn xử lý được;
        /// B5 nới ra nếu cần đo lại. Mặc định 48 giờ.
        /// </summary>
        public int LookbackHours { get; set; } = 48;
    }
}
