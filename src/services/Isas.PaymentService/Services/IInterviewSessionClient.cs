namespace Isas.PaymentService.Services
{
    /// <summary>
    /// R1 — ảnh chụp phía Interview cho một lô session_id đang giữ chỗ.
    ///
    /// ⚠ HAI TRƯỜNG NÀY KHÔNG THAY THẾ NHAU ĐƯỢC:
    /// <list type="bullet">
    /// <item><see cref="ExistingIds"/> = nguồn chân lý DUY NHẤT cho "session có tồn tại không". Interview
    /// bản CŨ (trước R1) cũng điền trường này ⇒ đọc nó thì Payment mới vẫn nói chuyện được với Interview cũ.</item>
    /// <item><see cref="States"/> = dữ liệu LÀM GIÀU, có thể RỖNG khi Interview còn bản cũ. Thiếu status cho
    /// một session ĐANG TỒN TẠI ⇒ SKIP; TUYỆT ĐỐI KHÔNG suy ra "session không tồn tại".</item>
    /// </list>
    /// Suy tồn-tại từ <see cref="States"/> là lỗi chết người: Interview cũ không trả <c>states</c> → mọi
    /// session trông như không tồn tại → reconciler release CẢ session đang thi (hoàn credit oan + thủng
    /// đúng bất biến an toàn của DB18). Lệch phiên bản giữa các image LÀ CHUYỆN ĐÃ XẢY RA trên hệ này.
    /// </summary>
    public sealed record InterviewSessionsSnapshot(
        IReadOnlySet<Guid> ExistingIds,
        IReadOnlyDictionary<Guid, string> States);

    /// <summary>
    /// DB18 — chiều gọi nội bộ Payment→Interview (trước đây Payment KHÔNG gọi Interview). Dùng bởi
    /// <c>OrphanReservationReconciler</c> để XÁC MINH DƯƠNG session nào thực sự tồn tại trước khi release
    /// chỗ giữ mồ côi. R1 — kèm trạng thái để phân nhánh chỗ giữ của session ĐÃ TERMINAL.
    /// </summary>
    public interface IInterviewSessionClient
    {
        /// <summary>
        /// Trả TẬP CON <paramref name="sessionIds"/> có row practice_sessions phía Interview (bất kể status),
        /// kèm trạng thái từng session khi Interview đủ mới (R1).
        /// Lỗi hạ tầng/non-2xx/JSON hỏng → NÉM <see cref="InterviewServiceException"/> (KHÔNG nuốt) → reconciler
        /// skip vòng, KHÔNG release ai. TUYỆT ĐỐI không coi "call lỗi" = "session không tồn tại".
        /// </summary>
        Task<InterviewSessionsSnapshot> GetExistingSessionsAsync(
            IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);
    }
}
