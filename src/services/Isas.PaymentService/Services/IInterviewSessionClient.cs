namespace Isas.PaymentService.Services
{
    /// <summary>
    /// DB18 — chiều gọi nội bộ Payment→Interview (trước đây Payment KHÔNG gọi Interview). Dùng bởi
    /// <c>OrphanReservationReconciler</c> để XÁC MINH DƯƠNG session nào thực sự tồn tại trước khi release
    /// chỗ giữ mồ côi.
    /// </summary>
    public interface IInterviewSessionClient
    {
        /// <summary>
        /// Trả TẬP CON <paramref name="sessionIds"/> có row practice_sessions phía Interview (bất kể status).
        /// Lỗi hạ tầng/non-2xx/JSON hỏng → NÉM <see cref="InterviewServiceException"/> (KHÔNG nuốt) → reconciler
        /// skip vòng, KHÔNG release ai. TUYỆT ĐỐI không coi "call lỗi" = "session không tồn tại".
        /// </summary>
        Task<HashSet<Guid>> GetExistingSessionsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);
    }
}
