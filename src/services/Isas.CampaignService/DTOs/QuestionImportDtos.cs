namespace Isas.CampaignService.DTOs;

/// <summary>
/// Kết quả đọc file CSV câu hỏi. Endpoint nhập CHỈ ĐỌC — không ghi gì vào cơ sở dữ liệu; HR xem trước
/// rồi bấm Lưu, và lượt Lưu đó đi qua <c>PUT /campaign/{id}/questions</c> sẵn có.
/// </summary>
public class ImportQuestionsResult
{
    /// <summary>Số dòng dữ liệu đọc được (không tính dòng tiêu đề, không tính dòng trắng).</summary>
    public int TotalRows { get; set; }

    /// <summary>
    /// Các dòng HỢP LỆ, giữ đúng thứ tự trong file.
    ///
    /// <para>Cố ý dùng lại <see cref="QuestionItem"/> — cùng kiểu mà <c>PUT /questions</c> nhận — nên
    /// giao diện nhồi thẳng danh sách này vào form rồi Lưu, không cần một tầng ánh xạ thứ hai. Phần tử
    /// ở đây không có <c>Id</c>: mọi dòng trong file là câu MỚI (xem hợp đồng merge F10).</para>
    /// </summary>
    public List<QuestionItem> Questions { get; set; } = new();

    /// <summary>
    /// Dòng hỏng. Không chặn cả file — bám tiền lệ sàng CV hàng loạt: một CV hỏng thì CV đó bị từ chối,
    /// không huỷ cả lô. HR sửa vài dòng dễ hơn tải lại từ đầu.
    /// </summary>
    public List<ImportRowError> Errors { get; set; } = new();
}

/// <param name="Line">
/// Số dòng TRONG FILE, tính cả dòng tiêu đề (tiêu đề = 1). HR mở lại file bằng Excel và nhảy đúng tới
/// dòng này — nên đây là số dòng của file, không phải chỉ số phần tử.
/// </param>
/// <param name="Column">Tên cột gây lỗi, null nếu lỗi thuộc về cả dòng.</param>
/// <param name="Message">Thông báo tiếng Việt, nói rõ sai gì và sửa thế nào.</param>
public record ImportRowError(int Line, string? Column, string Message);
