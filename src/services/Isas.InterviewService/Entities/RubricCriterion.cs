using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class RubricCriterion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    // Trọng số tính điểm tổng
    public decimal Weight { get; set; }

    // Thang điểm tối đa của tiêu chí (vd 5)
    public int MaxScore { get; set; }

    public bool IsActive { get; set; } = true;
    public JobCategory JobCategory { get; set; }
    public string Language { get; set; } = "vi";
    public int Version { get; set; } = 1;

    // Phạm vi chấm: Always = chấm mọi câu (tiêu chí CÁCH NÓI) · WhenTargeted = chỉ chấm khi câu hỏi
    // nhắm tới (tiêu chí NỘI DUNG). Lưu string (GEN-2).
    //
    // ⚠ Đây là CÁCH DUY NHẤT được phép để nhận diện nhóm "cách nói" — KHÔNG khớp theo TÊN tiếng Việt:
    // rubric tồn tại ở cả `vi` lẫn `en` (F12) và candidate tự đặt tên rubric riêng của mình (BC16),
    // nên so tên là một hợp đồng gãy ngay khi ai đó đổi một chữ.
    //
    // Mặc định `Always` (= hành vi trước thay đổi này) ⇒ mọi row KHÔNG được phân loại tường minh
    // (rubric riêng BC16, tiêu chí campaign B2B, row cũ) tự động an toàn: chấm thừa chứ không bỏ sót.
    public ScoringScope ScoringScope { get; set; } = ScoringScope.Always;

    // NGUỒN sinh ra điểm của tiêu chí này: LLM chấm từ transcript, hay hệ TỰ TÍNH từ số đo.
    // Lưu string (GEN-2).
    //
    // ⚠ Nhận diện bằng CỘT NÀY, KHÔNG khớp theo TÊN lúc chấm — y hệt ràng buộc đã ghi cho
    // `ScoringScope` ngay trên: rubric tồn tại ở cả `vi` lẫn `en` (F12) và candidate tự đặt tên
    // rubric riêng (BC16), nên so tên ở đường chấm là hợp đồng gãy ngay khi ai đó đổi một chữ.
    // (Kế thừa theo tên lúc GHI rubric riêng thì được — xem RubricLibraryService, cùng lý do
    // đã ghi ở đó cho `ScoringScope`: khớp trong đúng một (nghề, ngôn ngữ), trượt thì rơi về
    // mặc định = hành vi cũ.)
    //
    // Mặc định `Ai` (= hành vi trước thay đổi này) ⇒ tiêu chí campaign B2B và mọi row cũ không
    // đổi gì. Chiều mặc định cố ý là "vẫn nhờ LLM chấm": bật nhầm sang số đo cho một tiêu chí
    // NỘI DUNG sẽ thay điểm chuyên môn bằng một con số đo nhịp nói, sai mà không có triệu chứng.
    public CriterionScoringMethod ScoringMethod { get; set; } = CriterionScoringMethod.Ai;

    // B2B: tiêu chí thuộc về 1 campaign (thay cho job_category); null = rubric B2C theo JobCategory.
    // Ref lỏng sang CampaignService - KHÔNG FK xuyên service.
    public Guid? CampaignId { get; set; }

    // BC16 — B2C rubric CÁ NHÂN theo JobCategory. Ref lỏng sang AuthService (candidate), KHÔNG FK xuyên service.
    //  - CandidateId == null && CampaignId == null → seed mặc định dùng chung (BC11, fallback).
    //  - CandidateId != null && CampaignId == null → rubric riêng của candidate đó cho 1 nghề.
    // Scoring B2C ưu tiên rubric riêng (nếu có active), else fallback seed mặc định.
    public Guid? CandidateId { get; set; }

    // Navigation
    public ICollection<RubricLevel> Levels { get; set; } = [];
}
