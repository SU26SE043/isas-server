namespace Isas.CampaignService.Services
{
    /// <summary>
    /// CMP2-BE1 — một tiêu chí chấm của chiến dịch, gửi xuống AIService làm <b>BỐI CẢNH</b> cho lượt
    /// sinh câu hỏi ("buổi này sẽ được chấm bằng thước nào").
    ///
    /// <para><b>Chỉ mang <c>Name</c> + <c>Description</c> — CỐ Ý bỏ <c>Weight</c>/<c>MaxScore</c>/mốc
    /// điểm.</b> Hai thứ đó thuộc bài toán TÍNH ĐIỂM, không phải bài toán "nên hỏi gì"; đưa trọng số
    /// vào prompt là ngầm ra lệnh cho model phân bổ số câu theo trọng số — tức đúng ràng buộc PHỦ ĐỀU
    /// mà đợt này cố ý CHƯA làm (xem <see cref="IQuestionGenerator"/>). Cùng lý do
    /// <c>CriterionRef</c> bên AIService (chấm-theo-phạm-vi) cũng không mang <c>maxScore</c>/<c>weight</c>.</para>
    ///
    /// <para><b>Không mang <c>Id</c></b>: đây là bối cảnh một chiều, model KHÔNG trả nhãn nào về nên
    /// không có gì để map ngược. Ngày nào cần nhãn (task <c>SC2</c>) thì dùng đường
    /// <c>criteria</c>/<c>targetCriterionIds</c> vốn đã có sẵn, không mở rộng record này.</para>
    /// </summary>
    /// <param name="Name">Tên tiêu chí HR gõ — DỮ LIỆU, không phải lệnh (AI-4; AIService bọc delimiter).</param>
    /// <param name="Description">Mô tả HR gõ (có thể null/rỗng) — cũng là DỮ LIỆU.</param>
    public record QuestionCriterionContext(string Name, string? Description);

    /// <summary>
    /// F9 (FR11) — sinh câu hỏi phỏng vấn B2B từ JD của campaign, gọi AIService POST /api/v1/generate-questions.
    ///
    /// Vì sao KHÔNG fallback null như <see cref="ICriteriaSuggester"/>: tiêu chí có bộ mặc định hợp lý để
    /// fallback (publish vẫn chạy được), còn câu hỏi thì KHÔNG — "câu hỏi mặc định" là vô nghĩa với một JD
    /// cụ thể. HR bấm sinh mà AI hỏng thì phải BIẾT (502), không được nhận im lặng một danh sách rỗng/bịa.
    /// Mẫu này giống <see cref="IAiServiceFaceVerifyClient"/> (ném <see cref="DownstreamServiceException"/>).
    /// </summary>
    public interface IQuestionGenerator
    {
        /// <param name="jobCategory">Lĩnh vực (Domain campaign; rỗng → caller tự quyết mặc định).</param>
        /// <param name="jdText">JD dạng text — DỮ LIỆU đưa vào prompt, KHÔNG phải lệnh (AI-4; AIService bọc delimiter).</param>
        /// <param name="count">Số câu muốn sinh; null = để AIService dùng mặc định của nó.</param>
        /// <exception cref="DownstreamServiceException">AIService lỗi/không gọi được → caller map 502.</exception>
        /// <remarks>
        /// SEN1 — overload này gửi <c>seniority = "Junior"</c>. Overload dưới mới là đường truyền mức
        /// kinh nghiệm thật của chiến dịch.
        /// </remarks>
        Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default);

        /// <summary>
        /// SEN1 — như trên, kèm <paramref name="seniority"/> = mức kinh nghiệm HR đặt cấp CHIẾN DỊCH
        /// (<c>campaigns.seniority</c>: <c>Fresher|Junior|Middle|Senior</c>) để AIService hiệu chỉnh
        /// độ khó câu hỏi.
        /// </summary>
        /// <remarks>
        /// ⚠ <b>Vì sao là OVERLOAD chứ không phải thêm tham số có mặc định vào chữ ký cũ:</b> caller
        /// truyền <c>ct</c> ở <b>vị trí cuối cùng</b> theo lối positional, nên một tham số mặc định
        /// chèn vào trước <c>ct</c> vẫn làm nó vỡ biên dịch — giá trị mặc định không cứu được lời gọi
        /// positional.
        ///
        /// <para><c>ct</c> ở đây CỐ Ý không có giá trị mặc định: có mặc định thì lời gọi 3 tham số
        /// khớp được cả hai overload ⇒ CS0121 ambiguous.</para>
        ///
        /// <para>⚠ <b>KHÔNG có cài đặt mặc định — cố ý.</b> Bản SEN1 đầu để một default member bỏ qua
        /// <paramref name="seniority"/> cho hai test double khỏi vỡ biên dịch; nhưng đó đúng là kiểu hỏng
        /// SEN1 sinh ra để diệt — một implementer THẬT quên override sẽ đánh rơi mức kinh nghiệm mà
        /// <b>không lỗi nào, không test nào kêu</b>. Nay là thành viên bắt buộc: quên cài = vỡ BIÊN DỊCH,
        /// tức compiler làm việc kiểm thay cho người. Luật đó áp cho cả overload CMP2-BE1 bên dưới.</para>
        /// </remarks>
        Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority, CancellationToken ct);

        /// <summary>
        /// CMP2-BE1 — như trên, kèm <paramref name="criteriaContext"/> = bộ tiêu chí chấm của chiến dịch,
        /// để prompt biết buổi này <b>sẽ được chấm bằng thước nào</b>.
        /// </summary>
        /// <param name="criteriaContext">
        /// Tiêu chí campaign, đã sắp theo <c>order_no</c>. Rỗng ⇒ AIService giữ prompt NGUYÊN XI
        /// (chiến dịch Draft chưa khai tiêu chí vẫn sinh được câu hỏi y như trước).
        /// </param>
        /// <remarks>
        /// <b>Vấn đề nó chữa:</b> đo trên deploy, một buổi 3 câu gốc ra nhãn
        /// <c>Chiều sâu kỹ thuật</c> · <c>Chiều sâu kỹ thuật</c> · <c>Thiết kế hệ thống</c> ⇒ một tiêu
        /// chí không câu nào hỏi tới. Ở B2B mọi tiêu chí đều <c>Always</c> nên nó KHÔNG bị INT-18 loại
        /// khỏi điểm như bên B2C — nó bị chấm trên câu không liên quan, và mô hình bịa ra một con số.
        ///
        /// <para>🔴 <b>Đợt này CHỈ là BỐI CẢNH — KHÔNG ép "mỗi tiêu chí ít nhất một câu".</b> Bộ tiêu
        /// chí campaign không mang <c>scoring_scope</c> (bảng <c>campaign_criteria</c> không có cột đó),
        /// nên Campaign KHÔNG phân biệt được tiêu chí <i>cách nói</i> với tiêu chí <i>nội dung</i>; ép
        /// phủ đều sẽ đẻ ra câu hỏi phỏng vấn cho <i>"Ngữ pháp &amp; dùng từ"</i>. Ràng buộc phủ đều mở
        /// lại ở task <c>SC2</c>, khi B2B có scope thật.</para>
        ///
        /// <para>JD vẫn là neo CHÍNH — nó cụ thể hơn tiêu chí, và sinh câu chỉ từ tiêu chí sẽ ra câu
        /// chung chung hơn hiện nay. Prompt nhận CẢ HAI.</para>
        ///
        /// <para><c>ct</c> vẫn KHÔNG có mặc định, cùng lý do CS0121 ở overload trên.</para>
        /// </remarks>
        Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority,
            IReadOnlyList<QuestionCriterionContext> criteriaContext, CancellationToken ct);
    }
}
