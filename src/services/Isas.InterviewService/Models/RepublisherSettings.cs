namespace Isas.InterviewService.Models;

// DB29 — cấu hình StuckAnswerRepublisher (BackgroundService quét answer kẹt để đẩy lại job chấm).
public class RepublisherSettings
{
    public const string SectionName = "Republisher";

    // Trần số answer xử lý mỗi vòng quét. Trước DB29 truy vấn KHÔNG có Take() → sự cố broker dồn
    // hàng chục nghìn answer sẽ nạp hết (kèm transcript TEXT) vào bộ nhớ trong 1 vòng: chính component
    // sinh ra để CỨU quá tải lại là thứ gục trước. Batch có trần → mỗi vòng tiêu hoá 1 phần, vòng sau
    // lấy tiếp (quét mỗi 2') → vẫn thoát hàng, không nổ.
    public int BatchSize { get; set; } = 200;
}
