namespace Isas.InterviewService.Models;

// RAG grounding — cấu hình vận hành (KHÔNG thuộc F21 registry). Enabled mặc định TẮT: chưa nạp corpus
// thì bật chỉ tốn 1 embed/lượt mà retrieval luôn miss → giữ tắt để luồng cũ nguyên vẹn; L3 bật khi có corpus.
public class GroundingOptions
{
    public const string SectionName = "Grounding";

    // Bật retrieval khi sinh câu hỏi (B2C) + precompute roadmap lesson. Tắt → không đi đường grounding
    // (câu hỏi/lesson KHÔNG có field citations → FE không nhãn; hành vi trước grounding y nguyên).
    public bool Enabled { get; set; } = false;

    // Số chunk giữ lại mỗi lần retrieve.
    public int TopK { get; set; } = 4;

    // Ngưỡng similarity — Qdrant chỉ trả point ≥ ngưỡng (guard over-attribution: chunk yếu không lọt vào
    // tập grounding). Cosine ∈ [-1,1]; 0.5 = tương đối chặt. Chỉnh khi đo recall Phase 2.
    public float ScoreThreshold { get; set; } = 0.5f;
}
