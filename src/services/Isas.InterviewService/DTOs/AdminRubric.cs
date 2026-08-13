namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

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
public record AdminRubricCriterionInput(
    Guid Id,
    string? Description,
    /// <summary><c>null</c> hoặc <c>[]</c> = CHƯA khai mốc (⇒ chấm theo dải mặc định) — hợp lệ, không phải lỗi.</summary>
    List<AdminRubricLevelInput>? Levels
);

public record AdminRubricLevelInput(int Score, string Descriptor);

/// <summary>Thay nội dung bộ chuẩn của MỘT (nghề, ngôn ngữ). Phải gửi ĐỦ mọi tiêu chí đang có.</summary>
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
    IReadOnlyList<AdminRubricCriterionItem> Criteria
);

public record AdminRubricCriterionItem(
    Guid Id,
    string Name,
    string? Description,
    decimal Weight,
    int MaxScore,
    string ScoringScope,
    IReadOnlyList<AdminRubricLevelItem> Levels
);

public record AdminRubricLevelItem(int Score, string Descriptor);

/// <summary>
/// Một ô của ma trận 3 nghề × 2 ngôn ngữ ở đầu màn admin.
///
/// <para>Rủi ro lớn nhất của màn này không phải "rối" mà là BỎ SÓT: khai xong (BE, vi) rồi quên 5 tổ
/// hợp còn lại, và không có gì trên màn hình nói ra điều đó. <paramref name="WithLevelsCount"/> là
/// con số duy nhất trả lời được câu "còn thiếu ở đâu".</para>
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
