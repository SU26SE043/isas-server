namespace PaymentService.Models
{
    /// <summary>
    /// F22 (FR18) — một lượt gọi LLM: token vào/ra + quy ra tiền.
    ///
    /// VÌ SAO BẢNG NÀY NẰM Ở PAYMENT
    /// ──────────────────────────────
    /// AIService là chỗ BIẾT số token, nhưng GEN-4 cấm nó ghi DB, nên nó ĐẨY số liệu qua callback nội bộ
    /// (<c>POST /internal/ai-usage</c>, <c>X-Internal-Token</c>) — đúng cơ chế GEN-4 đã dựng sẵn cho kết quả
    /// AI. Người nhận là Payment vì chi phí AI là câu hỏi TIỀN và chỉ có nghĩa khi đọc cạnh doanh thu (F19
    /// cũng ở đây): "tháng này thu bao nhiêu, đốt bao nhiêu" phải trả lời được ở MỘT chỗ.
    ///
    /// KHÔNG PHẢI BẢNG TIỀN CỦA NGƯỜI DÙNG. Nó là chi phí vận hành:
    ///   • KHÔNG FK tới credit_accounts, KHÔNG ghi credit_transactions, KHÔNG đụng bất biến
    ///     <c>remaining + reserved = Σ delta</c>;
    ///   • KHÔNG có CHECK nào ràng buộc chéo với đường Consume/Release — cố ý, vì đó đúng là hình dạng lỗi
    ///     DB22 (một CHECK hợp lệ nổ giữa transaction tiền → rollback → reservation kẹt).
    /// Ghi hỏng dòng này KHÔNG được ảnh hưởng gì tới ví của ai.
    ///
    /// APPEND-ONLY, KHÔNG SỬA. Mỗi lượt gọi = một row.
    /// </summary>
    public class AiUsageLog
    {
        public Guid Id { get; set; }

        /// <summary>Đường gọi sinh ra chi phí (generate_questions · score · decide_next · text_to_speech …).
        /// Đây là thứ cho phép trả lời "tiền đi đâu", không chỉ "tốn bao nhiêu".</summary>
        public string Operation { get; set; } = string.Empty;

        /// <summary>Model thật sự đã chạy. TTS dùng model riêng và giá riêng — gộp chung sẽ tính sai.</summary>
        public string Model { get; set; } = string.Empty;

        public int PromptTokens { get; set; }
        public int OutputTokens { get; set; }

        /// <summary>Tổng do SDK trả, KHÔNG phải <c>prompt + output</c>: Gemini tính cả token nội bộ/suy luận
        /// nằm ngoài hai vế kia. Tự cộng lại là tự báo cáo thiếu.</summary>
        public int TotalTokens { get; set; }

        /// <summary>
        /// ĐƠN GIÁ SNAPSHOT tại thời điểm ghi (USD / 1 triệu token), mẫu <c>Invoice.UnitPrice</c>.
        /// Google đổi giá là chuyện sẽ xảy ra; nếu chỉ lưu token rồi nhân giá hiện hành lúc xem báo cáo thì
        /// mọi số liệu LỊCH SỬ tự động sai đi mỗi lần đổi giá — và sai một cách im lặng.
        /// </summary>
        public decimal InputPricePerMillionUsd { get; set; }
        public decimal OutputPricePerMillionUsd { get; set; }

        /// <summary>Tiền của riêng lượt gọi này, tính từ đơn giá đã snapshot ở trên.</summary>
        public decimal CostUsd { get; set; }

        /// <summary>
        /// F15 — số URL tài liệu AI ĐỀ XUẤT và số bị allowlist tên miền LOẠI. Chỉ có ở lượt sinh lý thuyết
        /// bài học; null ở mọi lượt khác (null ≠ 0: "không áp dụng" khác "đề xuất 0 link").
        /// Trước đó allowlist loại URL trong im lặng ⇒ không ai biết AI bịa tên miền nhiều hay ít, cũng
        /// không có cơ sở nào để nói allowlist 26 domain đang quá chặt hay quá lỏng.
        /// </summary>
        public int? ResourceUrlsProposed { get; set; }
        public int? ResourceUrlsRejected { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
