namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Bước 1 của HR technical screener — 1 nhu cầu công việc suy từ JD.
    ///
    /// Lưu jsonb trên <c>campaigns.job_needs</c> và materialize **một lần cho cả campaign**
    /// (lúc publish), KHÔNG suy lại theo từng CV: bước này chỉ đọc JD chứ không đọc CV, nên nó
    /// là thuộc tính của vị trí tuyển dụng. Suy lại mỗi CV thì không gì buộc hai lần đọc ra cùng
    /// bộ nhu cầu ⇒ hai ứng viên cùng campaign bị đo bằng hai cái thước khác nhau rồi xếp chung
    /// một bảng — đúng thứ bất công mà CAMP-10 chặn ở đường phỏng vấn.
    ///
    /// <c>NeedId</c> do CampaignService cấp (không phải AIService): đây mới là nơi lưu và nơi HR
    /// sửa, nên id sinh ở AIService sẽ chết ngay khi HR sửa/thêm một dòng.
    /// </summary>
    public class JobNeed
    {
        public string NeedId { get; set; } = null!;
        public string Category { get; set; } = null!;   // ∈ JobNeedCategories
        public string Text { get; set; } = null!;
        /// <summary>
        /// Nguồn gốc là sự thật do SERVER sở hữu — giá trị client gửi bị bỏ qua (bài học F10:
        /// cho client khai <c>source</c> thì HR tự dán nhãn "AI đề xuất" cho dòng mình gõ tay).
        /// </summary>
        public string Source { get; set; } = JobNeedSources.HrEdited;

        /// <summary>
        /// RNK1 · HĐ-6 — điều kiện LOẠI: thiếu bằng chứng Strong/Partial cho BẤT KỲ nhu cầu
        /// <c>IsMustHave</c> nào ⇒ ứng viên KHÔNG đủ điều kiện (<c>eligible = false</c>) ngay lúc
        /// sàng CV. Đánh giá READ-TIME từ (job_needs hiện tại ∩ strengths/gaps đã lưu) — KHÔNG cột,
        /// KHÔNG ghim: <c>job_needs</c> bị khoá sau khi có người sàng (<c>ReplaceJobNeedsAsync</c>)
        /// nên kết quả ổn định.
        ///
        /// ⚠ KHÁC <see cref="Source"/>: đây là quyết định NGHIỆP VỤ của HR (nhu cầu này bắt buộc
        /// hay không), không phải nhãn nguồn gốc ⇒ giá trị client GIỮ NGUYÊN. AI KHÔNG đề xuất
        /// (<c>BuildJobNeedsAsync</c> ép <c>false</c>). Vắng khoá trong jsonb (row trước RNK1) ⇒
        /// <c>false</c> ⇒ không loại ai — KHÔNG migration.
        /// </summary>
        public bool IsMustHave { get; set; }
    }

    public static class JobNeedCategories
    {
        public const string Technical = "Technical";
        public const string WorkStyle = "WorkStyle";
        public const string Communication = "Communication";
        public const string Growth = "Growth";

        public static readonly string[] All = { Technical, WorkStyle, Communication, Growth };

        public static bool IsValid(string? value) => value is not null && All.Contains(value);
    }

    public static class JobNeedSources
    {
        public const string AiSuggested = "AiSuggested";
        public const string HrEdited = "HrEdited";
    }

    /// <summary>
    /// Bước 2 — đánh giá CV theo 1 nhu cầu. <c>Evidence</c> là đoạn TRÍCH từ CV, không phải câu
    /// AI tự viết; không tìm thấy thì đúng câu <see cref="NeedEvidence.NotFound"/>.
    /// </summary>
    public class NeedAssessment
    {
        public string NeedId { get; set; } = null!;
        public string Area { get; set; } = null!;
        public string Level { get; set; } = null!;   // ∈ NeedLevels
        public string Evidence { get; set; } = null!;
    }

    public static class NeedLevels
    {
        public const string Strong = "Strong";     // bằng chứng trực tiếp, rõ ràng
        public const string Partial = "Partial";   // có dấu hiệu nhưng chưa đủ mạnh
        public const string Weak = "Weak";         // gần như không thấy bằng chứng

        public static readonly string[] All = { Strong, Partial, Weak };

        public static bool IsValid(string? value) => value is not null && All.Contains(value);

        /// <summary>
        /// Trọng số dùng để tính <c>jobFitScore</c>. Con số xếp hạng do ĐÂY tính chứ không hỏi AI:
        /// đo trên prod, bốn CV có bằng chứng GIỐNG HỆT nhau nhận điểm tổng do model phán là
        /// 70/70/55/55 — số holistic mâu thuẫn với chính bằng chứng model vừa liệt kê.
        ///
        /// Mức lạ ⇒ 0 (coi như chưa chứng minh được), KHÔNG phải 0.5: mọi hướng khác đều là cho
        /// không ứng viên một phần điểm mà không ai đọc được bằng chứng nào.
        /// </summary>
        public static decimal Credit(string? level) => level switch
        {
            Strong => 1m,
            Partial => 0.5m,
            _ => 0m,
        };
    }

    public static class NeedEvidence
    {
        /// <summary>
        /// Câu bắt buộc khi không tìm thấy bằng chứng — HẰNG SỐ chứ không phải câu model tự viết:
        /// nó phân biệt "đã tìm và không thấy" với "quên đánh giá", và HR đọc bảng thấy đúng một
        /// câu duy nhất thay vì mười cách diễn đạt khác nhau.
        /// ⚠ Phải trùng <c>NO_EVIDENCE</c> trong <c>Isas.AIService/app/schemas.py</c>.
        /// </summary>
        public const string NotFound = "Không thấy bằng chứng";
    }

    /// <summary>
    /// Bước 4 — mức cần kiểm chứng lại khi phỏng vấn.
    ///
    /// 🔴 CỐ Ý KHÔNG nhập vào <c>jobFitScore</c>: gộp hai thứ khác bản chất vào một con số là lặp
    /// lại đúng sai lầm bản này đang sửa — sau đó không ai giải thích được con số nữa. Nó đứng
    /// cạnh điểm dưới dạng cờ cho HR.
    /// </summary>
    public static class VerificationRisks
    {
        public const string Low = "Low";         // mô tả cụ thể: có thời gian, công nghệ, kết quả
        public const string Medium = "Medium";   // có công nghệ nhưng mô tả chung chung
        public const string High = "High";       // liệt kê nhiều kỹ năng mà không dự án nào chống lưng

        public static readonly string[] All = { Low, Medium, High };

        public static bool IsValid(string? value) => value is not null && All.Contains(value);
    }

    /// <summary>Con dấu thang điểm của <c>cv_submission.overall_match_score</c>.</summary>
    public static class ScreeningVersions
    {
        /// <summary>Điểm do LLM phán trên rubric buổi phỏng vấn (trước bản này).</summary>
        public const int LlmOverallMatch = 1;

        /// <summary>
        /// <c>jobFitScore</c> tính từ mức bằng chứng của từng nhu cầu công việc.
        /// Hai thang KHÔNG so sánh được với nhau — con dấu để chúng không bị trộn trong im lặng
        /// (tiền lệ <c>scoring_scope_version</c>/BK23).
        /// </summary>
        public const int JobFitFromEvidence = 2;
    }
}
