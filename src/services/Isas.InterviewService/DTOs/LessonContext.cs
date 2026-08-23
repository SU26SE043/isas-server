namespace Isas.InterviewService.DTOs;

/// <summary>
/// Ngữ cảnh BÀI HỌC của một buổi luyện sinh từ lộ trình — thứ nói cho AI biết buổi này hỏi về
/// CHỦ ĐỀ GÌ.
///
/// <para><b>Vấn đề đang sống trước khi có record này</b> (đo trên dev 2026-08-23): đường
/// <c>/start</c> chỉ gửi <c>lesson.Milestone.FocusCriteria</c> — tiêu chí của CHẶNG, không phải
/// của bài — nên MỌI bài trong cùng một chặng cho AI đúng một đầu vào. Chặng "Nền tảng Lập trình
/// &amp; Cấu trúc Dữ liệu" có <b>4 bài</b> (Ôn tập ngôn ngữ · Cấu trúc dữ liệu · Thuật toán tìm
/// kiếm/sắp xếp · Tổng quan OOP) dùng chung
/// <c>["Chiều sâu kỹ thuật","Giải quyết vấn đề &amp; thuật toán","Thuật ngữ chuyên ngành"]</c>;
/// trung bình 2,8 bài/chặng trên 87 chặng. Hệ quả đo được: bài "Phân tích và tối ưu hiệu năng
/// truy vấn SQL" nhận câu hỏi về xử lý lỗi API — chủ đề của bài KHÁC cùng chặng. Không lỗi,
/// không log: người học trả 1 credit cho một buổi hỏi lệch bài.
/// </summary>
/// <param name="Title">
/// Tiêu đề bài học. BẮT BUỘC — đây là tín hiệu phân biệt bài này với các bài cùng chặng.
/// </param>
/// <param name="Outline">
/// MỤC LỤC bài giảng (các đề mục <c>##</c>), gộp bằng xuống dòng. <c>null</c> khi người học bấm
/// "Bắt đầu" mà chưa mở bài lần nào (<c>theory_content</c> sinh LAZY lúc mở bài, còn
/// <c>StartLessonAsync</c> không đòi có nó) — hợp lệ, chỉ là mất một lớp ngữ cảnh.
///
/// <para>CỐ Ý KHÔNG gửi nguyên bài giảng: đo trên dev, <c>theory_content</c> trung bình
/// <b>14.310</b> ký tự (tối đa 47.655) — nhét vào prompt sinh câu hỏi vừa đắt mỗi buổi vừa lấn át
/// tiêu chí trọng tâm. Mục lục trung bình 7,4 đề mục ≈ 300 ký tự, giữ được đúng thứ cần: bài này
/// gồm những phần nào.</para>
/// </param>
public record LessonContext(string Title, string? Outline);
