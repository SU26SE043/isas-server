namespace Isas.InterviewService.Enums;

// Phỏng vấn THÍCH ỨNG — phân loại nguồn của câu hỏi trong buổi.
//  Seed        = câu hỏi mở đầu (B2C: AI sinh; B2B: campaign cấp). Ai cũng nhận cùng bộ seed.
//  FollowUp    = AI đào sâu trong cùng năng lực dựa trên câu trả lời vừa rồi.
//  Clarify     = AI hỏi làm rõ câu trả lời chưa rõ/thiếu ý.
//  NewQuestion = AI chuyển sang năng lực/tiêu chí khác (còn ngân sách).
// Lưu string (GEN-2). Rows cũ backfill 'Seed'.
public enum QuestionKind
{
    Seed,
    FollowUp,
    Clarify,
    NewQuestion
}
