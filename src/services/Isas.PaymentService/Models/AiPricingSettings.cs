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

        public class ModelPrice
        {
            public decimal InputPerMillionUsd { get; set; }
            public decimal OutputPerMillionUsd { get; set; }
        }
    }
}
