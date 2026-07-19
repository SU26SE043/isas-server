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
}
