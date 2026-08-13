using System.Text.Json;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Isas.InterviewService.Configurations;

public class RubricCriterionConfiguration : IEntityTypeConfiguration<RubricCriterion>
{
    public void Configure(EntityTypeBuilder<RubricCriterion> e)
    {
        // DB19 — đúng-1-owner: cấm ĐỒNG THỜI campaign_id (B2B) và candidate_id (B2C, BC16).
        // 3 trạng thái loại trừ hợp lệ: campaign-only · candidate-only · both-null (seed mặc định BC11).
        // Không đường code nào set cả 2 (RubricLibraryService=candidate-only · PracticeService=campaign-only
        // · seed=both-null) → CHECK chỉ chặn dữ liệu bẩn, không phá luồng hiện có.
        e.ToTable("rubric_criteria", t =>
        {
            t.HasCheckConstraint(
                "ck_rubric_criteria_single_owner",
                "campaign_id IS NULL OR candidate_id IS NULL");

            // DB15 — weight ∈ (0,1]: khớp code (RubricLibraryService chuẩn hoá Σweight=1 nên mỗi tiêu
            // chí >0; seed BC11 mỗi tiêu chí ≤1). Chặn dữ liệu bẩn (weight ≤0 hoặc >1) ở tầng DB.
            t.HasCheckConstraint(
                "ck_rubric_criteria_weight_range",
                "weight > 0 AND weight <= 1");
            t.HasCheckConstraint(
                "ck_rubric_criteria_language",
                "language IN ('vi', 'en')");

            // Phạm vi chấm lưu string (GEN-2) → CHECK chặn giá trị lạ ở tầng DB (mẫu ck_..._language).
            t.HasCheckConstraint(
                "ck_rubric_criteria_scoring_scope",
                "scoring_scope IN ('Always', 'WhenTargeted')");
        });

        e.HasKey(x => x.Id);

        e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        e.Property(x => x.Weight).HasColumnType("numeric(5,4)").IsRequired();
        e.Property(x => x.MaxScore).IsRequired();
        e.Property(x => x.IsActive).IsRequired();

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();
        e.Property(x => x.Language).HasColumnType("text").IsRequired().HasDefaultValue("vi");

        // Phạm vi chấm — string (GEN-2) + default 'Always' ở TẦNG DB, để `AddColumn` của migration tự
        // điền cho mọi row đang có (rubric riêng BC16 + tiêu chí campaign B2B) mà không cần backfill tay.
        //
        // ⚠ Độ dài 24 cho chuỗi dài nhất 12 ký tự ('WhenTargeted') — bài học S11: `funded_by`
        // varchar(16) gặp enum 19 ký tự làm VỠ mọi lượt ghi trên Postgres trong khi SQLite (test)
        // KHÔNG enforce độ dài nên toàn bộ test vẫn xanh. Chừa gấp đôi.
        e.Property(x => x.ScoringScope)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired()
            .HasDefaultValue(ScoringScope.Always);

        e.Property(x => x.Version).IsRequired();

        e.HasIndex(x => new { x.JobCategory, x.Version, x.IsActive });

        // B2B: đọc/materialize tiêu chí theo campaign. Non-unique, nullable.
        e.HasIndex(x => x.CampaignId);

        // Chống NHÂN ĐÔI tiêu chí khi materialize đua nhau: hai ứng viên bấm Start cùng lúc ngay sau
        // khi HR bump phiên bản rubric ⇒ cả hai đều thấy "chưa có bộ v2" ⇒ cả hai cùng chèn ⇒ campaign
        // có hai bộ v2, mẫu số điểm tổng (INT-10) sai mà không lỗi nào nổ. Ràng buộc DB là thứ duy
        // nhất chặn được ca này.
        //
        // Filter `campaign_id IS NOT NULL` là BẮT BUỘC, không phải tối ưu: rubric B2C có
        // campaign_id = NULL và trùng `name` khắp nơi (mỗi candidate một bộ "Giao tiếp & trình bày"),
        // nên unique không lọc sẽ chặn oan toàn bộ đường rubric riêng BC16.
        // An toàn về ngữ nghĩa vì Campaign vốn đã có UNIQUE (campaign_id, name).
        e.HasIndex(x => new { x.CampaignId, x.Version, x.Name })
            .IsUnique()
            .HasFilter("campaign_id IS NOT NULL")
            .HasDatabaseName("ux_rubric_criteria_campaign_version_name");

        // Cùng lớp bảo vệ như index ngay trên, cho BỘ CHUẨN B2C do admin quản: hai admin cùng bấm Lưu
        // trên một (nghề, ngôn ngữ) sẽ cùng đọc `max(version)` ra một số rồi cùng ghi ⇒ 14 dòng active
        // cùng lúc ⇒ loader nạp 14 tiêu chí và `criteria[0].Version` phụ thuộc may rủi. Đọc
        // `max(version)` KHÔNG phải trọng tài; ràng buộc DB là thứ duy nhất chặn được.
        //
        // Filter `candidate_id IS NULL` BẮT BUỘC: rubric riêng BC16 đánh số version độc lập theo từng
        // ứng viên, nên hai người khác nhau hoàn toàn được phép trùng (nghề, ngôn ngữ, version, tên).
        //
        // ⚠ SQLite (EF Core 10) THẬT SỰ dựng index có filter qua `EnsureCreated` và enforce nó — đã đo
        // bằng mutation (gỡ index ⇒ test chuyển ĐỎ), nên L1 ở đây KHÔNG phải xanh giả. Nhưng đó là may
        // mắn về ngữ nghĩa trùng nhau giữa hai engine, không phải bảo đảm: L3 Postgres vẫn là nơi duy
        // nhất chứng minh câu filter chạy đúng trên bản thật.
        e.HasIndex(x => new { x.JobCategory, x.Language, x.Version, x.Name })
            .IsUnique()
            .HasFilter("campaign_id IS NULL AND candidate_id IS NULL")
            .HasDatabaseName("ux_rubric_criteria_b2c_default_version_name");

        // BC16: tra rubric riêng của candidate theo nghề (resolve ưu-tiên-riêng-else-mặc-định).
        // Non-unique, nullable (null = seed mặc định dùng chung).
        e.HasIndex(x => new { x.CandidateId, x.JobCategory, x.Language, x.IsActive });

        e.HasMany(x => x.Levels)
            .WithOne(l => l.Criterion)
            .HasForeignKey(l => l.CriterionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RubricLevelConfiguration : IEntityTypeConfiguration<RubricLevel>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<RubricLevel> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Score).IsRequired();
        e.Property(x => x.Descriptor).IsRequired();

        e.HasIndex(x => new { x.CriterionId, x.Score }).IsUnique();

        // DB15 — anchor (câu trả lời mẫu neo mức) gộp thành jsonb string[] trên chính rubric_levels
        // (thay bảng rubric_anchors 1-n). Non-null; converter → JSON string nên SQLite (test) serialize
        // ra text == Postgres jsonb. Mẫu giống RoadmapMilestone.FocusCriteria (converter + comparer).
        var listConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>());

        var listComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        var examples = e.Property(x => x.ExampleAnswers);
        examples.HasConversion(listConverter);
        examples.Metadata.SetValueComparer(listComparer);
        examples.HasColumnType("jsonb");
        examples.IsRequired();
    }
}
