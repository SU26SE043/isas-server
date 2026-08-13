namespace Isas.CampaignService.Validation;

/// <summary>
/// Trần độ dài / số lượng cho câu hỏi campaign và cho lượt nhập hàng loạt bằng CSV.
///
/// <para><b>Vì sao đặt riêng ở đây, không nhét vào <c>Isas.Shared.Validation.TextInputLimits</c>:</b>
/// cái kia là ngưỡng CHUNG cho JD/criteria, áp cho cả B2B lẫn B2C và cả Interview lẫn Campaign
/// (CAMP-5). Câu hỏi campaign chỉ CampaignService dùng — gộp vào ngưỡng chung sẽ buộc mọi service
/// khác nhận theo một con số chẳng liên quan gì tới chúng.</para>
///
/// <para><b>Vì sao trước đây không có:</b> <c>question_text</c> chưa từng bị giới hạn ở tầng nào —
/// cột là <c>text</c>, controller và service chỉ kiểm rỗng. Chấp nhận được khi HR gõ tay từng câu;
/// nhưng khi có nhập bằng file thì một file 5 MB một dòng cũng lọt vào tận prompt gửi cho AI.</para>
/// </summary>
public static class QuestionLimits
{
    /// <summary>Câu hỏi phỏng vấn, không phải bài luận.</summary>
    public const int QuestionTextMaxChars = 2_000;

    /// <summary>
    /// ~800 từ. Trần này đặt sẵn ngân sách token cho ngày đáp án mẫu được ghép vào prompt chấm:
    /// 200 câu × 5.000 ký tự là cận trên biết trước, thay vì phát hiện lúc hoá đơn AI tăng.
    /// </summary>
    public const int SampleAnswerMaxChars = 5_000;

    /// <summary>Tên nhóm chủ đề — khớp <c>HasMaxLength(100)</c> của cột.</summary>
    public const int QuestionGroupMaxChars = 100;

    /// <summary>
    /// Trần số câu MỘT chiến dịch.
    /// <para>⚠ Phải áp ở <c>UpdateCampaignQuestionsAsync</c>, KHÔNG chỉ ở parser CSV: chặn mỗi parser
    /// là chặn hình thức — client gọi thẳng <c>PUT /questions</c> với 5.000 phần tử vẫn lọt.</para>
    /// <para>Khác bản chất với <c>MaxGeneratedQuestions = 20</c> (CampaignService): con số đó là trần
    /// CHI PHÍ một lượt gọi AI, con số này là trần KÍCH THƯỚC ngân hàng đề.</para>
    /// </summary>
    public const int MaxQuestionsPerCampaign = 200;

    /// <summary>Số dòng dữ liệu tối đa một file nhập — bằng trần ngân hàng đề ở trên.</summary>
    public const int ImportMaxRows = MaxQuestionsPerCampaign;

    /// <summary>
    /// 200 dòng × (2.000 + 5.000) ký tự tiếng Việt ≈ 2,8 MB khi mã hoá UTF-8 → 5 MB là rộng gấp đôi.
    /// </summary>
    public const int ImportMaxFileBytes = 5 * 1024 * 1024;
}
