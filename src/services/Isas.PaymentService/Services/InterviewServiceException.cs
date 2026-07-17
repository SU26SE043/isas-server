namespace Isas.PaymentService.Services
{
    /// <summary>
    /// DB18 — lỗi khi Payment gọi InterviewService `/internal/sessions/exists` (hạ tầng/non-2xx/JSON hỏng).
    /// NÉM (không nuốt) để <c>OrphanReservationReconciler</c> bắt ở vòng ngoài → SKIP cả vòng, KHÔNG release
    /// ai — không xác minh được session tồn tại thì TUYỆT ĐỐI không coi là orphan (tránh release oan
    /// reservation hợp lệ khi Interview down).
    /// </summary>
    public class InterviewServiceException : Exception
    {
        public InterviewServiceException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
