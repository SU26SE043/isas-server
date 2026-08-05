namespace Isas.PaymentService.Models
{
    /// <summary>
    /// F22 — bảng giá token, bind section <c>AiPricing</c>.
    ///
    /// GIÁ NẰM Ở PAYMENT, KHÔNG Ở AISERVICE: AIService biết SỐ TOKEN (việc của nó), Payment biết GIÁ TIỀN
    /// (việc của nó — cùng chỗ với <c>product_packages.price_vnd</c>). Chia thế thì đổi giá là sửa cấu hình
    /// một service, không phải deploy lại AIService; và tiền chỉ được tính ở đúng một nơi.
    ///
    /// Giá là dữ liệu SẼ ĐỔI (Google đổi bảng giá), nên nó KHÔNG được hardcode và mỗi dòng usage phải giữ
    /// bản snapshot của đơn giá đã dùng — xem <see cref="PaymentService.Models.AiUsageLog"/>.
    ///
    /// Đơn vị: USD cho 1 TRIỆU token (đúng cách Google niêm yết) → khỏi phải đọc số 0.000000075 rồi tự hỏi
    /// mình đếm đúng mấy số 0 chưa.
    ///
    /// HAI ĐƠN VỊ TÍNH TIỀN, MỘT BẢNG GIÁ. Không phải model nào cũng tính theo token: model chép lời
    /// (<c>whisper-1</c>) tính theo PHÚT AUDIO. Cả hai đơn vị nằm chung một entry/model thay vì tách
    /// <c>Models</c> ‖ <c>AudioModels</c> — hai dictionary song song đẻ ra câu hỏi "model này khai ở đâu",
    /// và khai nhầm chỗ thì model rơi về <see cref="Default"/> mà không có gì kêu.
    /// </summary>
    public class AiPricingSettings
    {
        /// <summary>Giá theo tên model, key = đúng chuỗi model AIService gửi lên (vd <c>gemini-2.5-flash</c>).</summary>
        public Dictionary<string, ModelPrice> Models { get; set; } = new();

        /// <summary>
        /// Giá dùng khi model chưa có trong <see cref="Models"/>. KHÔNG để null: model lạ (đổi model, thêm
        /// model TTS mới) sẽ được ghi với giá mặc định + cảnh báo log, còn hơn là ghi cost = 0 và làm báo
        /// cáo chi phí trông đẹp một cách sai sự thật.
        /// </summary>
        public ModelPrice Default { get; set; } = new();

        public ModelPrice Resolve(string model) =>
            Models.TryGetValue(model, out var price) ? price : Default;

        /// <summary>
        /// Đơn giá THEO PHÚT của một model, có bậc thang: giá riêng của model → giá mặc định → null.
        ///
        /// Bậc thang là cần thiết vì hai lý do lệch nhau: model có trong bảng nhưng KHÔNG khai giá phút
        /// (gemini — nó tính theo token, đúng như vậy), và model KHÔNG có trong bảng (whisper-2 mai này).
        /// Cả hai ca đều phải rơi về giá mặc định chứ không được rơi về 0, vì 0 làm chi phí biến mất khỏi
        /// báo cáo trong im lặng. Trả null khi không tìm được gì để nơi gọi còn CẢNH BÁO ĐƯỢC — chứ không
        /// tự bịa ra một con số.
        /// </summary>
        public decimal? ResolvePerMinute(string model) =>
            (Models.TryGetValue(model, out var price) ? price.PricePerMinuteUsd : null)
            ?? Default.PricePerMinuteUsd;

        public class ModelPrice
        {
            public decimal InputPerMillionUsd { get; set; }
            public decimal OutputPerMillionUsd { get; set; }

            /// <summary>
            /// USD cho 1 PHÚT audio. <c>null</c> = model này không tính theo phút (đa số model sinh chữ) —
            /// khác 0, vốn nghĩa là "tính theo phút, và miễn phí".
            /// </summary>
            public decimal? PricePerMinuteUsd { get; set; }
        }
    }
}
