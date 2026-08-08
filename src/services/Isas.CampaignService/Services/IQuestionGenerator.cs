namespace Isas.CampaignService.Services
{
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
        /// ⚠ <b>Vì sao là OVERLAP chứ không phải thêm tham số có mặc định vào chữ ký cũ:</b> caller duy
        /// nhất (<c>CampaignService.GenerateQuestionsAsync</c>) truyền <c>ct</c> ở <b>vị trí thứ 4</b>,
        /// nên một tham số mặc định chèn vào trước <c>ct</c> vẫn làm nó vỡ biên dịch — giá trị mặc định
        /// không cứu được lời gọi positional. Mà file caller đó nằm NGOÀI phạm vi sở hữu của thay đổi
        /// này (worker khác đang giữ).
        ///
        /// <para>✅ <c>CampaignService.GenerateQuestionsAsync</c> nay gọi overload này với
        /// <c>campaign.Seniority</c> ⇒ B2B đã thật sự gửi mức của chiến dịch.</para>
        ///
        /// <para><c>ct</c> ở đây CỐ Ý không có giá trị mặc định: có mặc định thì lời gọi 3 tham số
        /// khớp được cả hai overload ⇒ CS0121 ambiguous.</para>
        ///
        /// <para>⚠ <b>KHÔNG có cài đặt mặc định — cố ý.</b> Bản SEN1 đầu để một default member bỏ qua
        /// <paramref name="seniority"/> cho hai test double khỏi vỡ biên dịch; nhưng đó đúng là kiểu hỏng
        /// SEN1 sinh ra để diệt — một implementer THẬT quên override sẽ đánh rơi mức kinh nghiệm mà
        /// <b>không lỗi nào, không test nào kêu</b>. Nay là thành viên bắt buộc: quên cài = vỡ BIÊN DỊCH,
        /// tức compiler làm việc kiểm thay cho người.</para>
        /// </remarks>
        Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority, CancellationToken ct);
    }
}
