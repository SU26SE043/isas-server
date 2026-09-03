namespace Isas.CampaignService.Validation;

/// <summary>
/// RNK1 · HĐ-7 — ràng buộc CHÉO giữa 3 số adaptive B2B, để "cùng trần độ sâu" thật sự cho ra
/// "cùng số câu" giữa các ứng viên cùng campaign (fairness — CAMP-10, xếp hạng chung).
///
/// <para><b>Vì sao cần:</b> ở chế độ chuỗi (INT-17b, <c>d = maxDeepPerQuestion &gt; 0</c>) mỗi câu
/// gốc được đào sâu tối đa <c>d</c> lần, nên MỘT buổi cần <c>K × (1 + d)</c> khe (K câu gốc +
/// K×d câu đào sâu). Nếu trần buổi <c>T = maxQuestions</c> nhỏ hơn số đó, ngân sách cạn theo
/// <b>thứ tự trả lời</b> (<c>AnswerService.cs</c>) ⇒ chuỗi nào chạm trước được đào đủ, phần còn
/// lại cụt — hai ứng viên trả lời khác thứ tự nhận số câu + chủ đề đào sâu khác nhau.</para>
///
/// <para><b>Thuần</b> — không ném, không đọc DB. Caller (create/update/publish) tự ném
/// <see cref="Isas.CampaignService.Services.AdaptiveBudgetTooSmallException"/> khi có
/// <see cref="Violation"/>, để controller trả 400 body <c>{ code, need, have, questions, deep }</c>.</para>
/// </summary>
public static class AdaptiveBudgetRule
{
    public const string Code = "ADAPTIVE_BUDGET_TOO_SMALL";

    /// <param name="k">Số câu GỐC 1 buổi rút cho ứng viên = <c>questionsPerSession ?? số câu campaign</c>.</param>
    /// <param name="d">Trần đào sâu MỖI câu gốc (<c>maxDeepPerQuestion</c>). <c>0</c> = không phải chế độ chuỗi.</param>
    /// <param name="t">Trần TỔNG câu 1 buổi (<c>maxQuestions</c>). <c>0</c> = KHÔNG có trần buổi (Interview lo).</param>
    /// <returns><c>null</c> = hợp lệ; ngược lại là <see cref="Violation"/> mang 4 số cho body 400.</returns>
    public static Violation? Check(int k, int d, int t)
    {
        // d ≤ 0: không phải chế độ chuỗi ⇒ không ràng buộc.
        // t ≤ 0: không có trần buổi ⇒ ngân sách "vô hạn" ⇒ mọi chuỗi đủ khe.
        if (d <= 0 || t <= 0)
            return null;

        var need = k * (1 + d);
        return t < need ? new Violation(Need: need, Have: t, Questions: k, Deep: d) : null;
    }

    /// <summary>4 số cho body 400 (khoá JSON camelCase: <c>need · have · questions · deep</c>).</summary>
    public sealed record Violation(int Need, int Have, int Questions, int Deep);
}
