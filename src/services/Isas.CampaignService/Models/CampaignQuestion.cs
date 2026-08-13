namespace Isas.CampaignService.Models
{
    public class CampaignQuestion
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid OrgId { get; set; }   // BK4: owner denormalize theo campaign = ORG (AUTH-8)
        public string QuestionText { get; set; }
        public QuestionSource Source { get; set; }
        public bool IsRequired { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// R10 — mốc HR sửa NỘI DUNG một câu do AI sinh (null = chưa ai sửa).
        ///
        /// Vì sao phải là cột riêng chứ không đổi <see cref="Source"/> sang <c>CustomHr</c> khi HR sửa:
        /// F10 khoá "provenance là sự thật do SERVER sở hữu, không đổi khi HR sửa" bằng test — badge
        /// "AI sinh" phải sống sót qua vòng đọc→sửa→lưu. Hai thông tin này khác nhau: <c>Source</c> trả lời
        /// "ai VIẾT RA câu này", <c>HrEditedAt</c> trả lời "HR có bỏ công chỉnh nó không".
        ///
        /// F9 ("sinh lại") xoá mọi row <c>AiGenerated</c> — có cột này mới phân biệt được câu AI HR chưa
        /// đụng tới (thay được) với câu AI HR đã chỉnh (mất là mất trắng công sức, không khôi phục được).
        /// </summary>
        public DateTime? HrEditedAt { get; set; }

        /// <summary>
        /// Đáp án mẫu HR soạn cho câu này (null = chưa soạn).
        ///
        /// Tên `sample_answer` chứ không phải `expected_answer`: "expected" hàm ý bộ chấm đối chiếu để
        /// tìm câu trả lời ĐÚNG DUY NHẤT. Nó không làm thế — nó chỉ được cấp cho AI như một ví dụ tốt
        /// để hiệu chỉnh thang điểm. Thống nhất với <c>practice_answers.sample_answer</c> bên Interview.
        /// </summary>
        public string? SampleAnswer { get; set; }

        /// <summary>
        /// Nhóm chủ đề HR khai (vd "Thuật toán", "Thiết kế hệ thống"). null = nhóm mặc định.
        ///
        /// Chỉ có nghĩa khi campaign bật ngân hàng đề (<see cref="Campaign.QuestionsPerSession"/>):
        /// mỗi buổi rút ĐỀU theo nhóm thay vì rút mù. Vì sao phải đều: INT-18 loại tiêu chí không câu
        /// nào hỏi tới ra khỏi điểm (không tính 0) ⇒ rút mù thì ứng viên A bốc 4 câu thuật toán bị chấm
        /// gắt mảng đó, còn B bốc 0 câu thì mảng đó BIẾN MẤT khỏi điểm của B — rồi hai người xếp chung
        /// một bảng (CAMP-10). Đó là đo bằng hai thước khác nhau, không phải "đề khác nhau chút".
        /// </summary>
        public string? QuestionGroup { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
    }

    public enum QuestionSource
    {
        AiGenerated = 0,
        CustomHr = 1
    }
}
