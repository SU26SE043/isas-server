using System.Globalization;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// BE-6 — tên hiển thị của lộ trình.
///
/// Vì sao SERVER sinh tên mặc định chứ không để client tự chế: trước bản này backend không có tên
/// nào, nên FE rơi vào nhánh dự phòng `|| 'Roadmap'` (roadmapMapper.ts:204) 100% số lần — người
/// dùng có ba lộ trình thì thấy ba thẻ giống hệt nhau, chỉ khác ngày. Sinh ở đây thì mọi client
/// (web, app di động sau này) thấy CÙNG một tên, và tên nằm trong payload nên tìm kiếm/sắp xếp
/// về sau đều dựa vào một nguồn.
/// </summary>
public static class RoadmapNaming
{
    /// <summary>Trần độ dài tên. Khớp giữa đường tạo, đường đổi tên và cột DB — lệch nhau thì
    /// người dùng gõ được ở màn này mà bị 400 ở màn kia.</summary>
    public const int MaxLength = 120;

    // Tên nghề đầy đủ. Giữ khớp `prompts.py:82` (`"BA": "Business Analyst"`) — cùng một khái niệm
    // hiện ra cho người dùng ở hai nơi thì phải cùng một chữ.
    private static string JobDisplay(JobCategory job) => job switch
    {
        JobCategory.BA => "Business Analyst",
        JobCategory.BE => "Backend Developer",
        JobCategory.FE => "Frontend Developer",
        _ => job.ToString(),
    };

    // Cấp độ GIỮ NGUYÊN tiếng Anh ở cả hai ngôn ngữ — Fresher/Junior/Middle/Senior là thuật ngữ
    // ngành mà tin tuyển dụng tiếng Việt cũng dùng thẳng. Dịch ra ("Sơ cấp"/"Cao cấp") sẽ tạo
    // thêm một bảng chữ thứ hai phải giữ đồng bộ với FE, mà tên này người dùng sửa được nên
    // không đáng đánh đổi.
    private static string LevelDisplay(RoadmapLevel level) => level.ToString();

    /// <summary>
    /// Tên mặc định khi người dùng không tự đặt: nghề · cấp độ · ngày tạo.
    ///
    /// ⚠ Ngày định dạng theo NGÔN NGỮ CỦA LỘ TRÌNH, không theo văn hoá của máy chủ. Repo đã dính
    /// đúng lỗi này một lần (F16): bản xuất PDF in `91,5` còn CSV in `91.5` cho cùng một campaign
    /// vì cả hai đọc culture của process — cùng dữ liệu, hai kết quả, đổi theo máy chạy.
    /// </summary>
    public static string BuildDefault(JobCategory job, RoadmapLevel level, string language, DateTime createdAtUtc)
    {
        var isEn = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var culture = CultureInfo.GetCultureInfo(isEn ? "en-US" : "vi-VN");
        var day = createdAtUtc.ToString(isEn ? "d MMM yyyy" : "dd/MM/yyyy", culture);

        return isEn
            ? $"{JobDisplay(job)} roadmap · {LevelDisplay(level)} · {day}"
            : $"Lộ trình {JobDisplay(job)} · {LevelDisplay(level)} · {day}";
    }

    /// <summary>
    /// Tên để TRẢ RA API. Hàng tạo trước BE-6 có `name` null — suy tên tại chỗ lúc đọc thay vì để
    /// null chảy ra ngoài. Để null lọt ra là tái tạo đúng bug đang sửa: client lại phải tự đoán,
    /// và mỗi client đoán một kiểu.
    /// </summary>
    public static string Resolve(string? stored, JobCategory job, RoadmapLevel level, string language, DateTime createdAtUtc)
        => string.IsNullOrWhiteSpace(stored)
            ? BuildDefault(job, level, language, createdAtUtc)
            : stored;

    /// <summary>
    /// Chuẩn hoá tên người dùng gửi lên. Trả `null` khi client KHÔNG gửi field (caller tự sinh mặc định).
    ///
    /// ⚠ Chuỗi rỗng / toàn khoảng trắng là GIÁ TRỊ SAI, không phải "không gửi" — ném để caller trả 400.
    /// Đây đúng lớp lỗi BK36 vừa phải vá ở BA chỗ: `IsNullOrWhiteSpace` gộp hai ca đó làm một khiến
    /// `language: ""` âm thầm thành "vi". Ở đây gộp lại sẽ khiến người dùng gõ tên toàn dấu cách rồi
    /// nhận một cái tên máy sinh mà không hiểu vì sao tên mình biến mất.
    /// </summary>
    public static string? Normalize(string? requested)
    {
        if (requested is null) return null;

        var name = requested.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Tên lộ trình không được để trống.");
        if (name.Length > MaxLength)
            throw new InvalidOperationException($"Tên lộ trình tối đa {MaxLength} ký tự (đang gửi {name.Length}).");

        return name;
    }
}
