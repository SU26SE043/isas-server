using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// BC12 — milestone của 1 roadmap. order_no UNIQUE(roadmap_id, order_no). focus_criteria = snapshot
// tên tiêu chí trọng tâm (rubric đổi version không hồi tố). improvement set khi Completed (BC15).
public class RoadmapMilestone
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoadmapId { get; set; }
    public Roadmap Roadmap { get; set; } = null!;

    public int OrderNo { get; set; }

    public string Title { get; set; } = null!;

    // jsonb string[] — tên tiêu chí milestone này tập trung cải thiện (từ AI /generate-roadmap).
    public List<string> FocusCriteria { get; set; } = [];

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Pending;

    // jsonb? — { criterionName: deltaPct } so baseline / mile trước; set khi Completed (BC15). BC12 null.
    public Dictionary<string, decimal>? Improvement { get; set; }

    public DateTime? CompletedAt { get; set; }

    // jsonb? — phần TÍNH đã chốt cùng lúc với Improvement (xem MilestoneScoreSnapshot). null = chặng
    // hoàn thành trước bản này (KHÔNG BIẾT), hoặc chặng chưa hoàn thành.
    public MilestoneScoreSnapshot? ScoreSnapshot { get; set; }

    // Navigation — Cascade theo milestone_id.
    public ICollection<RoadmapLesson> Lessons { get; set; } = [];
}

/// <summary>
/// Phần TÍNH đã chốt sổ của một chặng: điểm từng tiêu chí + đúng những buổi đã cộng vào + mốc so.
/// Trả lời câu hỏi "con số −20% kia ở đâu ra".
///
/// <para><b>Vì sao SNAPSHOT chứ không tính lại lúc đọc:</b> con số hiện ở tiêu đề lấy từ
/// <see cref="RoadmapMilestone.Improvement"/> — đã chốt lúc chặng hoàn thành. Tính lại từ dữ liệu
/// HIỆN TẠI thì chỉ cần một lần luyện lại bài (<c>/retry</c>) là hai bên lệch nhau: tiêu đề nói
/// −20%, phần tính cộng ra số khác — đúng thứ tính năng này sinh ra để chống. Cột này được ghi
/// trong CÙNG một <c>ExecuteUpdate</c> với <c>Improvement</c>, từ CÙNG một vòng lặp, nên hai bên
/// không thể lệch nhau <b>do cấu trúc</b>, không phải nhờ cẩn thận.</para>
///
/// <para><b>null = KHÔNG BIẾT</b> (BK23): chặng hoàn thành TRƯỚC bản này không có snapshot. Đường
/// đọc KHÔNG được im lặng vẽ nó thành dữ liệu chốt — xem <c>source</c> trong
/// <c>MilestoneScoreReportResponse</c>.</para>
/// </summary>
public record MilestoneScoreSnapshot(
    // "previousMilestone" | "baseline" | "none" — xem MilestoneScoreReference.
    string ComparedWith,
    string? ComparedWithTitle,
    List<MilestoneScoreCriterionSnapshot> Criteria);

/// <summary>Điểm một tiêu chí của chặng + phần tính ra nó.</summary>
/// <param name="CurrentAveragePercentage">
/// Trung bình cộng <b>đúng</b> các dòng liệt kê trong <paramref name="CurrentSessions"/> (làm tròn 2).
/// </param>
/// <param name="ReferenceAveragePercentage">
/// Điểm mốc để so. <c>null</c> = tiêu chí này không có mốc ⇒ <paramref name="DeltaPct"/> cũng null.
/// </param>
/// <param name="ReferenceSessions">
/// Các buổi của mốc — CHỈ có khi mốc là chặng liền trước. Mốc <c>baseline</c> là một snapshot số
/// (đo lúc lập lộ trình), không có buổi nào đứng sau nó ⇒ rỗng.
/// </param>
/// <param name="DeltaPct">
/// <c>current − reference</c>. <c>null</c> = KHÔNG CÓ MỐC — <b>không được</b> thay bằng 0: 0 nghĩa
/// là "không tiến bộ", còn đây là "chưa có gì để so".
/// </param>
public record MilestoneScoreCriterionSnapshot(
    string Name,
    decimal CurrentAveragePercentage,
    List<MilestoneScoreSessionSnapshot> CurrentSessions,
    decimal? ReferenceAveragePercentage,
    List<MilestoneScoreSessionSnapshot> ReferenceSessions,
    decimal? DeltaPct);

/// <summary>
/// Một buổi luyện đã cộng vào điểm của tiêu chí.
/// </summary>
/// <param name="AttemptNo">
/// Lần làm thứ mấy của bài đó (<c>roadmap_lesson_attempts</c>). <c>null</c> khi buổi không có dòng
/// lần-làm nào trỏ tới (dữ liệu trước khi có bảng đó).
///
/// <para>Cần nó vì điểm chặng chỉ đếm buổi MỚI NHẤT của mỗi bài: thấy "Lần 2" thì người học hiểu
/// ngay lần 1 đã bị thay thế, thay vì tưởng hệ thống làm mất một buổi.</para>
/// </param>
/// <param name="ScoredAt">
/// Mốc buổi được CHẤM (<c>max(session_criterion_scores.created_at)</c> của buổi) — cùng mốc mà
/// <c>progress[]</c> của báo cáo lộ trình đang dùng, KHÔNG phải <c>completed_at</c> (lúc bấm nộp,
/// và nullable).
/// </param>
public record MilestoneScoreSessionSnapshot(
    Guid SessionId,
    string LessonTitle,
    int? AttemptNo,
    decimal Percentage,
    DateTime ScoredAt);

/// <summary>Mốc so của một chặng. Hằng số để chuỗi trên dây và chuỗi trong code không trôi khỏi nhau.</summary>
public static class MilestoneScoreReference
{
    public const string PreviousMilestone = "previousMilestone";
    public const string Baseline = "baseline";
    /// <summary>Không có mốc nào dùng được ⇒ mọi <c>deltaPct</c> là null (KHÔNG phải 0).</summary>
    public const string None = "none";
}

/// <summary>Số trong báo cáo chặng từ đâu ra — xem <c>MilestoneScoreReportResponse.Source</c>.</summary>
public static class MilestoneScoreSource
{
    /// <summary>Đọc từ cột đã chốt cùng lúc với con số ở tiêu đề ⇒ không thể lệch.</summary>
    public const string Snapshot = "snapshot";
    /// <summary>Chặng chưa hoàn thành: tính lúc đọc, chưa có số chốt nào để mà lệch.</summary>
    public const string Computed = "computed";
    /// <summary>Chặng hoàn thành TRƯỚC bản này (không có snapshot): tính lại từ dữ liệu HIỆN TẠI.</summary>
    public const string Recomputed = "recomputed";
}
