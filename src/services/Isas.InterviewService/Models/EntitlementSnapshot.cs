namespace Isas.InterviewService.Models;

/// <summary>Stable, local representation of Payment's resolved B2C entitlement.</summary>
public sealed record EntitlementSnapshot(
    string Source, string TierCode, int TierRank, bool AdaptiveEnabled, int MaxQuestions,
    int MaxFollowUps, bool GroundingEnabled, int SelfConsistencyN, bool CvAnalysisIncluded,
    bool RepoAnalysisIncluded, bool RoadmapEnabled)
{
    /// <summary>
    /// SÀN dùng khi Payment không trả lời được (timeout / non-2xx / JSON hỏng) — và cũng là giá trị
    /// giữ chỗ khi <c>Tiering:Enabled=false</c>.
    ///
    /// <para><b>Adaptive = <c>true</c></b>: adaptive là ENGINE phỏng vấn chứ không phải quyền lợi theo
    /// gói (mọi tier đều tiêu đúng 1 credit/buổi), nên Payment sập KHÔNG được biến một buổi đã trừ
    /// credit thành buổi luồng tĩnh trong im lặng.</para>
    ///
    /// <para><b>Trần <c>0</c> nghĩa là "gói KHÔNG khai trần riêng"</b>, không phải "0 câu":
    /// <see cref="Services.PracticeService"/> rơi về trần cấu hình <c>Adaptive:MaxQuestions</c>. Đừng
    /// chép một con số cứng vào đây — thành hai nguồn sự thật cho cùng một trần.</para>
    ///
    /// Các quyền lợi CÓ chi phí biên khác nhau thật (grounding · self-consistency · CV/repo · roadmap)
    /// vẫn fail-closed.
    /// </summary>
    public static readonly EntitlementSnapshot Free = new(
        "free-default", "free", 0, true, 0, 0, false, 1, false, false, false);
}
