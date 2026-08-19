namespace Isas.InterviewService.Models;

// F14 (FR08) — mốc đối chiếu vẽ thành lớp thứ hai trên radar kết quả buổi luyện.
//
// ⚠ ĐỌC TRƯỚC KHI ĐỔI: hệ thống KHÔNG có dữ liệu "chuẩn ngành" nào cả. Không mua bộ dữ liệu
// benchmark, không tích hợp nguồn ngoài. Vì vậy mốc ở đây CHỈ được lấy từ hai nguồn có thật:
//
//   1. PeerAverage — trung bình của chính người dùng khác trên hệ thống (cùng vị trí, B2C, đã
//      chấm, KHÔNG tính bản thân). Đây là số thật, đo được, và nhãn nói đúng nó là gì.
//   2. PassThreshold — ngưỡng đạt NỘI BỘ (ScoringOptions.ImprovementThresholdPct), tức đúng cái
//      ngưỡng đang quyết định tiêu chí nào bị gắn "cần cải thiện" trên chính màn hình đó.
//
// TUYỆT ĐỐI không gắn nhãn "chuẩn ngành" cho hai thứ trên. Bịa một con số rồi trình bày như
// chuẩn ngành là nói dối người dùng về mức độ tin cậy của thứ họ đang nhìn.
public class BenchmarkOptions
{
    public const string SectionName = "Benchmark";

    // false → API không trả benchmark → radar chỉ còn 1 lớp (hành vi trước F14).
    public bool Enabled { get; set; } = true;

    // Số buổi luyện CỦA NGƯỜI KHÁC tối thiểu cho MỖI tiêu chí thì trung bình cộng đồng mới có
    // nghĩa. Dưới ngưỡng → rơi về PassThreshold.
    //
    // VÌ SAO loại bản thân khỏi mẫu: so mình với một tập có chứa chính mình là vòng tròn, và ở
    // ca "hệ thống mới có đúng 1 người dùng" thì tập đó CHÍNH LÀ họ ⇒ mốc luôn trùng khít điểm
    // của họ, vô nghĩa mà nhìn lại rất thuyết phục. Loại bản thân khiến ca đó tự động ra n=0 →
    // rơi về ngưỡng nội bộ, thay vì hiển thị một sự trùng khớp giả.
    public int MinSampleSize { get; set; } = 5;

    // CỬA SỔ THỜI GIAN của mẫu cộng đồng — chỉ tính buổi tạo trong N ngày gần đây.
    //
    // ⚠ ĐÂY LÀ PHÒNG XA, KHÔNG PHẢI SỬA SỰ CỐ. Bảng `session_criterion_scores` hiện mới vài trăm
    // dòng nên truy vấn mẫu KHÔNG phải nguyên nhân của bất kỳ độ trễ nào đang đo được. Vấn đề là
    // hình dạng của nó: mẫu được nạp HẾT vào RAM rồi mới gom (bắt buộc — xem ghi chú "không AVG
    // SQL" trong CriterionBenchmarkService), nên chi phí tăng TUYẾN TÍNH theo toàn bộ lịch sử của
    // MỌI người cùng nghề, và nó chạy đúng lúc buổi vừa `Scored` (lần poll trả báo cáo) rồi lặp
    // lại mỗi lần mở trang Kết quả. Ở 100k buổi × ~7 tiêu chí là ~700k dòng cho MỖI lượt xem.
    //
    // Cửa sổ đổi trần đó từ "toàn bộ lịch sử" thành "lưu lượng N ngày" — một hằng số theo nhịp sử
    // dụng chứ không theo tuổi đời sản phẩm. Nó còn làm con số ĐÚNG NGHĨA HƠN: "trung bình người
    // luyện cùng vị trí" nên là người đang luyện gần đây, không phải trung bình trộn cả những buổi
    // chấm bằng rubric/prompt của một năm trước.
    //
    // 90 ngày = một quý: đủ dài để sản phẩm còn ít người dùng vẫn gom nổi `MinSampleSize`, đủ ngắn
    // để "gần đây" không thành lời nói suông. `<= 0` = tắt cửa sổ (lấy toàn bộ lịch sử — hành vi cũ).
    public int PeerWindowDays { get; set; } = 90;

    // TTL cache mẫu cộng đồng (giây). 0 (hoặc âm) = TẮT cache, tính lại mỗi lượt.
    //
    // Mẫu này KHÔNG phụ thuộc người xem (xem ghi chú "trừ phần của chính mình" trong service): mọi
    // người cùng (nghề, ngôn ngữ, bộ tên tiêu chí) đọc CÙNG một tổng. Tính lại cho từng lượt xem là
    // lãng phí thuần — nhất là khi FE poll trạng thái buổi rồi mở trang Kết quả ngay sau đó.
    //
    // TTL NGẮN vì mốc cộng đồng không cần tươi theo giây: 5 phút chỉ làm cửa sổ trượt chậm hơn tối
    // đa 5 phút trên một cửa sổ 90 ngày. Đổi lại, một buổi mới chấm xong nhìn thấy mốc "cũ 5 phút" —
    // chấp nhận được, vì mốc là trung bình của hàng trăm buổi, không phải số của riêng ai.
    public int CacheTtlSeconds { get; set; } = 300;
}
