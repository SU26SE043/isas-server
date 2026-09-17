namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;
using System.Text.Json.Serialization;
/// <summary>
/// Một tiêu chí của BỘ CHUẨN hệ thống, ở dạng admin được phép GỬI LÊN.
///
/// <para>🔴 Bốn trường <c>Name</c> · <c>Weight</c> · <c>MaxScore</c> · <c>ScoringScope</c> CỐ Ý KHÔNG
/// có mặt ở đây. Đó là cách bịt bằng CẤU TRÚC: gán nhầm chúng từ payload sẽ là lỗi biên dịch, không
/// phải một guard chạy lúc chạy mà ai đó có thể gỡ. Lý do từng trường:</para>
/// <list type="bullet">
/// <item><b>Name</b> — BC12 (điểm yếu → lộ trình ôn), BC15 (đo cải thiện) và F14 (mốc so với người
/// khác) đều gom nhóm THEO TÊN. Đổi tên một tiêu chí là cắt đôi chuỗi thời gian của MỌI người dùng,
/// im lặng và không hoàn lại được.</item>
/// <item><b>Weight</b> — B2C tính điểm tổng bằng trung bình cộng (INT-10) nên sửa nó không đổi được
/// điểm gì, mà lại phải giữ Σ = 1.</item>
/// <item><b>MaxScore</b> — đổi thang là mọi mốc phải khai lại, và <c>percentage</c> lịch sử hết so
/// sánh được.</item>
/// <item><b>ScoringScope</b> — phá bất biến 4 <c>Always</c> / 3 <c>WhenTargeted</c> mỗi nghề mà
/// <c>B2CRubricSeedTests</c> đang khoá, và làm hỏng việc gắn nhãn câu hỏi (INT-18).</item>
/// </list>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record AdminRubricCriterionInput(
    Guid Id,
    string? Description,
    /// <summary><c>null</c> hoặc <c>[]</c> = CHƯA khai mốc (⇒ chấm theo dải mặc định) — hợp lệ, không phải lỗi.</summary>
    List<AdminRubricLevelInput>? Levels
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record AdminRubricLevelInput(int Score, string Descriptor);

/// <summary>Thay nội dung bộ chuẩn của MỘT (nghề, ngôn ngữ). Phải gửi ĐỦ mọi tiêu chí đang có.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record UpsertAdminRubricRequest(List<AdminRubricCriterionInput> Criteria);

/// <param name="Changed">
/// <c>false</c> = nội dung không khác gì bản đang chạy nên KHÔNG tạo phiên bản mới. Bump khi không ai
/// sửa gì làm nhãn phiên bản mất nghĩa và cắt vụn quota chấm thử (vốn tính theo phiên bản).
/// </param>
public record AdminRubricResponse(
    JobCategory JobCategory,
    string Language,
    int Version,
    bool Changed,
    IReadOnlyList<AdminRubricCriterionItem> Criteria,
    /// <summary>
    /// Câu mẫu dùng được cho chấm thử ở đúng (nghề, ngôn ngữ) này — client CHỌN từ đây rồi gửi
    /// <c>sampleQuestionId</c>.
    ///
    /// <para>Trả kèm ở đây thay vì để client tự biết: nội dung câu mẫu phải tồn tại ở ĐÚNG MỘT chỗ.
    /// Chép sang giao diện là hai nguồn sự thật — sửa câu ở backend thì màn hình vẫn hiện câu cũ và
    /// không gì báo.</para>
    /// </summary>
    IReadOnlyList<AdminSampleQuestionItem> SampleQuestions
);

public record AdminSampleQuestionItem(string Id, string Text);

public record AdminRubricCriterionItem(
    Guid Id,
    string Name,
    string? Description,
    decimal Weight,
    int MaxScore,
    string ScoringScope,
    /// <summary>
    /// <c>Ai</c> (LLM chấm từ transcript — mốc là THƯỚC ĐO, AI phải chọn một mức) hay
    /// <c>DeliveryMetrics</c> (tính từ số đo giọng nói F11 qua <see cref="Services.DeliveryFluencyScorer"/>,
    /// KHÔNG gửi LLM — mốc chỉ là LỜI GIẢI NGHĨA bậc, không tham gia tính điểm, chấm thử cũng không đòi).
    ///
    /// <para>Vì sao lộ ra: FE từng chặn cứng chấm thử khi có tiêu chí &lt;2 mốc trong khi BE chỉ đòi mốc ở
    /// tiêu chí AI chấm (<see cref="Services.MeasuredCriteriaSplit.ForAi"/>) — "Độ trôi chảy" cố ý 0 mốc
    /// nên màn admin báo thiếu một thứ không cần (đo trên dev 2026-09-16). Không lộ trường này thì UI
    /// chỉ còn cách đoán theo TÊN, mà tên đổi theo ngôn ngữ.</para>
    /// </summary>
    string ScoringMethod,
    IReadOnlyList<AdminRubricLevelItem> Levels
);

public record AdminRubricLevelItem(int Score, string Descriptor);

/// <summary>
/// Một ô của ma trận 3 nghề × 2 ngôn ngữ ở đầu màn admin.
///
/// <para>Rủi ro lớn nhất của màn này không phải "rối" mà là BỎ SÓT: khai xong (BE, vi) rồi quên 5 tổ
/// hợp còn lại, và không có gì trên màn hình nói ra điều đó. <paramref name="WithLevelsCount"/> là
/// con số duy nhất trả lời được câu "còn thiếu ở đâu".</para>
///
/// <para>⚠ Cả <paramref name="CriteriaCount"/> lẫn <paramref name="WithLevelsCount"/> chỉ đếm tiêu chí
/// <b>CẦN mốc</b> (<c>ScoringMethod = Ai</c>). Tiêu chí đo bằng số đo giọng nói (F11) cố ý 0 mốc — đếm nó
/// vào mẫu số là ô ma trận báo "thiếu mốc" vĩnh viễn cho một thứ không cần. Cùng luật ở
/// <see cref="AdminRubricVersionItem"/>.</para>
/// </summary>
public record AdminRubricMatrixRow(
    JobCategory JobCategory,
    string Language,
    int Version,
    int CriteriaCount,
    int WithLevelsCount
);

/// <summary>Một phiên bản trong lịch sử của (nghề, ngôn ngữ). Append-only ⇒ đây là dấu vết đầy đủ.</summary>
public record AdminRubricVersionItem(
    int Version,
    bool IsActive,
    int CriteriaCount,
    int WithLevelsCount
);
