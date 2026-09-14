namespace Isas.InterviewService.Services;

/// <summary>
/// Một dòng điểm thô đã materialize, kèm CÂU GỐC HIỆU DỤNG của answer sinh ra nó.
///
/// <para><c>RootQuestionId</c> ở đây là <b>gốc hiệu dụng</b> — <c>question.RootQuestionId ?? question.Id</c>,
/// đúng công thức <c>PracticeQuestion.RootQuestionId</c> tự khai và đúng công thức
/// <c>AnswerService</c> dùng khi nối chuỗi. Câu gốc trỏ về CHÍNH NÓ, câu đào sâu trỏ về gốc của chuỗi.</para>
/// </summary>
public sealed record AnswerCriterionScore(
    Guid AnswerId,
    Guid RootQuestionId,
    Guid CriterionId,
    decimal Score);

/// <summary>
/// ADP1 — gộp <c>answer_scores</c> của một buổi thành điểm trung bình MỖI TIÊU CHÍ.
///
/// <para><b>Ba bước, theo đúng thứ tự:</b></para>
/// <code>
/// median mỗi (answer, criterion)        ← E10 self-consistency, KHÔNG đổi
///   → trung bình mỗi (CÂU GỐC, criterion) ← ADP1: chuỗi đào sâu gộp về câu gốc của nó
///     → trung bình qua các CÂU GỐC
/// </code>
///
/// <para><b>Vì sao thêm bước giữa:</b> INT-17b cho mỗi câu gốc một chuỗi đào sâu riêng, dài ngắn
/// khác nhau tuỳ ứng viên trả lời thế nào. Trước bản này mỗi answer là một quan sát ngang hàng, nên
/// trong CÙNG một buổi:</para>
/// <code>
/// câu gốc bị đào sâu 3 lần  →  đóng góp 4 quan sát vào điểm
/// câu gốc không đào sâu     →  đóng góp 1 quan sát
/// </code>
/// <para>tức chủ đề nào ứng viên bị hỏi kỹ hơn thì tự động nặng gấp bốn lần trong điểm tổng, mà độ
/// dài chuỗi lại do AI quyết lúc thi chứ không phải do thước đo ai khai. <b>Câu gốc cộng chuỗi đào
/// sâu của nó là MỘT đơn vị đánh giá, không phải bốn.</b></para>
///
/// <para><b>Tự lùi an toàn, KHÔNG cần cờ bật/tắt:</b> chế độ frontier (kill-switch
/// <c>MaxDeepPerQuestion = 0</c>) để <c>RootQuestionId = null</c> trên mọi câu nối thêm
/// (<c>AnswerService</c>: <c>perQuestionMode ? rootQuestionId : null</c>), và câu gốc bù TU1 cũng
/// vậy ⇒ ở đó mỗi answer là gốc RIÊNG của chính nó ⇒ bước giữa gom nhóm một-phần-tử ⇒ công thức
/// cho ra <b>đúng con số của hành vi cũ</b>. Buổi cũ (mọi câu <c>RootQuestionId = null</c>) cũng vậy.
/// Thêm một cờ cấu hình ở đây chỉ đẻ ra hai đường điểm phải cùng đúng mãi mãi.</para>
///
/// <para><b>Vì sao là hàm DÙNG CHUNG:</b> hai đường gọi — chấm B2C (<c>SessionResultService</c>, ghi
/// <c>session_criterion_scores</c> + <c>overall_score</c>) và chấm B2B/tổng
/// (<c>SessionScoringNotifier</c>, dựng <c>TotalScore</c> đi vào event xếp hạng) — PHẢI cho ra cùng
/// một con số cho cùng một buổi. Hai đoạn nhân bản sẽ trôi xa nhau; tiền lệ <c>SkipPenaltyRule.Apply</c>
/// tách hàm chung cho đúng tình huống này.</para>
///
/// <para><b>Tiêu chí không có dòng điểm nào ⇒ KHÔNG có khoá trong kết quả</b> (không phải 0). Cả hai
/// caller đều <c>continue</c> khi <c>TryGetValue</c> trượt — đó là cách INT-18 loại tiêu chí "không ai
/// hỏi" ra khỏi điểm thay vì cho nó 0. Giữ nguyên tính chất đó ở đây.</para>
/// </summary>
public static class CriterionScoreAggregator
{
    /// <summary>
    /// ĐÃ BIẾT: mỗi <c>answer</c> là một quan sát ngang hàng ⇒ chuỗi đào sâu dài đóng góp nhiều
    /// phiếu hơn chuỗi ngắn. Hành vi TRƯỚC ADP1.
    ///
    /// <para>⚠ Code hiện tại KHÔNG BAO GIỜ ghi giá trị này — nó chỉ tồn tại để giữ nghĩa cho ô số 1,
    /// và để phân biệt "biết chắc là cách cũ" với <c>null</c> = "không biết". Buổi chấm trước bản
    /// này mang <c>null</c>, KHÔNG được backfill thành 1 (xem migration).</para>
    /// </summary>
    public const int VersionPerAnswer = 1;

    /// <summary>
    /// ĐÃ BIẾT: chuỗi đào sâu gộp về CÂU GỐC ⇒ mỗi câu gốc một phiếu, dài mấy cũng vậy (ADP1).
    /// </summary>
    public const int VersionPerRootQuestion = 2;

    /// <summary>
    /// Con dấu mà <b>bản code này</b> đóng lên mọi buổi nó chấm. Đặt cạnh chính thuật toán chứ không
    /// ở service gọi: đổi cách gộp ở đây mà quên đổi số thì con dấu nói dối, và con dấu nói dối tệ
    /// hơn không có con dấu — nó trả lời "hai điểm này cùng thước đo không?" một cách SAI mà tự tin.
    /// </summary>
    public const int CurrentVersion = VersionPerRootQuestion;

    /// <summary>
    /// Trả về <c>criterionId → điểm THÔ trung bình</c> (chưa chuẩn hoá theo <c>maxScore</c>, chưa
    /// gộp trọng số — hai việc đó thuộc về từng caller vì B2C dùng equal-weight còn B2B dùng weighted).
    ///
    /// <para>Chạy hoàn toàn trong bộ nhớ: caller đã <c>ToListAsync</c> trước khi gọi. Median không dịch
    /// được sang SQL, và <c>Average(decimal)</c> trên SQLite map <c>ef_avg</c> dễ lệch Postgres —
    /// gộp ở client là chủ đích, không phải quên tối ưu. Tập dữ liệu là một buổi (≤ vài chục answer).</para>
    /// </summary>
    public static Dictionary<Guid, decimal> AverageByCriterion(
        IReadOnlyCollection<AnswerCriterionScore> rawScores)
    {
        ArgumentNullException.ThrowIfNull(rawScores);

        return rawScores
            // (1) E10 — điểm chốt mỗi (answer, criterion) = MEDIAN qua các attempt self-consistency.
            //     N=1 → median-of-1 = chính giá trị đó (không đổi hành vi).
            //     RootQuestionId là thuộc tính của ANSWER nên mọi dòng trong nhóm mang cùng giá trị.
            .GroupBy(s => (s.AnswerId, s.CriterionId))
            .Select(g => new
            {
                g.First().RootQuestionId,
                g.Key.CriterionId,
                Score = ScoreStatistics.Median(g.Select(x => x.Score))
            })
            // (2) ADP1 — chuỗi đào sâu gộp về CÂU GỐC: cả chuỗi là một quan sát, dài mấy cũng vậy.
            .GroupBy(s => (s.RootQuestionId, s.CriterionId))
            .Select(g => new { g.Key.CriterionId, Score = g.Average(x => x.Score) })
            // (3) Trung bình qua các câu gốc ĐÃ CHẠM tiêu chí này. Câu gốc mà chuỗi của nó không sinh
            //     dòng điểm nào cho tiêu chí X thì không có mặt ở nhóm X — đúng như trước đây answer
            //     không có điểm cho X thì không tham gia (INT-18: không tính 0 cho thứ không được hỏi).
            .GroupBy(s => s.CriterionId)
            .ToDictionary(g => g.Key, g => g.Average(x => x.Score));
    }
}
