namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;
using System.Text.Json.Serialization;
/// <summary>
/// Một tiêu chí của BỘ CHUẨN hệ thống, ở dạng admin GỬI LÊN (RUB1, 2026-10-02 — mở từ BC-8 bản đầu).
///
/// <para><b>Ngữ nghĩa từng trường:</b></para>
/// <list type="bullet">
/// <item><c>Id</c> — <c>null</c> = tiêu chí MỚI; có giá trị = tiêu chí của bộ đang hiệu lực (id lạ ⇒ 400).
/// Tiêu chí đang có mà KHÔNG xuất hiện trong body = bị XOÁ khỏi phiên bản mới.</item>
/// <item><c>Name</c> / <c>Weight</c> / <c>ScoringScope</c> — <c>null</c> = giữ nguyên (chỉ với id có sẵn);
/// tiêu chí mới bắt buộc có đủ ba trường.</item>
/// <item><c>Description</c> — <c>null</c>/rỗng = KHÔNG có mô tả (ngữ nghĩa cũ, KHÔNG phải "giữ nguyên").</item>
/// <item><c>Levels</c> — <c>null</c>/<c>[]</c> = chưa khai mốc (⇒ dải mặc định), hợp lệ.</item>
/// </list>
///
/// <para>🔴 <c>MaxScore</c> và <c>ScoringMethod</c> VẪN CỐ Ý KHÔNG có ở đây — bịt bằng CẤU TRÚC: gán nhầm
/// từ payload là lỗi biên dịch. Thang 0–5 cố định cho bộ chuẩn B2C (đổi thang = mọi mốc phải khai lại
/// và <c>percentage</c> lịch sử hết so sánh được); tiêu chí mới luôn <c>Ai</c> + thang 5; tiêu chí đo bằng
/// số đo (F11) không được đổi tên/phạm vi.</para>
///
/// <para>⚠ Vì sao trước đây khoá <c>Name</c>/<c>Weight</c>/<c>ScoringScope</c> và nay mở: <c>Weight</c> không
/// làm đổi điểm vì B2C tính trung bình cộng — từ RUB1 buổi mới tính CÓ TRỌNG SỐ (INT-10) nên trọng số
/// là cần gạt thật. <c>Name</c> vẫn mang hệ quả cũ: BC12/BC15/F14 gom theo TÊN ⇒ đổi tên là lịch sử
/// tiến bộ của tiêu chí đó bắt đầu lại — đây là quyết định có ý thức của admin, không phải lỗi.</para>
///
/// <para>Thứ tự tham số giữ <c>(Id, Description, Levels)</c> ở đầu, ba trường mới optional ở CUỐI: mọi
/// call-site cũ (test) biên dịch không sửa và ngữ nghĩa cũ ("chỉ sửa mô tả + mốc") được giữ nguyên.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record AdminRubricCriterionInput(
    Guid? Id,
    string? Description,
    /// <summary><c>null</c> hoặc <c>[]</c> = CHƯA khai mốc (⇒ chấm theo dải mặc định) — hợp lệ, không phải lỗi.</summary>
    List<AdminRubricLevelInput>? Levels,
    string? Name = null,
    decimal? Weight = null,
    /// <summary><c>"Always"</c> | <c>"WhenTargeted"</c>; <c>null</c> = giữ nguyên.</summary>
    string? ScoringScope = null
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record AdminRubricLevelInput(int Score, string Descriptor);

/// <summary>
/// Nội dung MỚI của bộ chuẩn một (nghề, ngôn ngữ). Tiêu chí đang có mà vắng khỏi danh sách = bị xoá.
/// Σweight phải = 1 (±0.0001, sau khi làm tròn 4 chữ số) — KHÔNG tự chuẩn hoá.
/// </summary>
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
