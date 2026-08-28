using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Isas.InterviewService.Configurations;

// BC12 (D20) — 3 bảng roadmap. Cột snake_case tự sinh (UseSnakeCaseNamingConvention).
// jsonb (focus_criteria/baseline/source_session_ids/improvement) lưu qua value converter → JSON string
// (test SQLite == Postgres). Enum lưu string. FK Cascade theo roadmap_id → milestone → lesson;
// cv_id / session_id → Restrict.
public class RoadmapConfiguration : IEntityTypeConfiguration<Roadmap>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>F15 — options dùng chung cho jsonb của lesson (xem <c>RoadmapLessonConfiguration</c>).</summary>
    internal static readonly JsonSerializerOptions LessonJson = Json;

    // Comparer cho jsonb collection nullable — chặn warning 10620 + đúng change-tracking (BC15 set lại).
    internal static readonly ValueComparer<List<Guid>> GuidListComparer = new(
        (a, b) => (a ?? new List<Guid>()).SequenceEqual(b ?? new List<Guid>()),
        v => v == null ? 0 : v.Aggregate(0, (h, g) => HashCode.Combine(h, g.GetHashCode())),
        v => v.ToList());

    // Expression tree không cho `out var` → tách helper static cho equals/hash.
    internal static readonly ValueComparer<Dictionary<string, decimal>> DecimalDictComparer = new(
        (a, b) => DictEquals(a, b),
        v => DictHash(v),
        v => v.ToDictionary(kv => kv.Key, kv => kv.Value));

    private static bool DictEquals(Dictionary<string, decimal>? a, Dictionary<string, decimal>? b)
    {
        a ??= new Dictionary<string, decimal>();
        b ??= new Dictionary<string, decimal>();
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var bv) || bv != kv.Value) return false;
        return true;
    }

    private static int DictHash(Dictionary<string, decimal>? v)
    {
        if (v is null) return 0;
        var h = 0;
        foreach (var kv in v.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            h = HashCode.Combine(h, kv.Key.GetHashCode(), kv.Value.GetHashCode());
        return h;
    }

    // MIS1-B4 — jsonb List<string>? NULLABLE dùng chung cho MistakeRefs (milestone + lesson).
    // Mẫu GroundingRefs (dưới): provider `string?` (có dấu ?) — thiếu dấu ? thì EF coi cột required,
    // scaffold ra `nullable: false`, dính lỗi `defaultValue` đã biết (F15).
    internal static readonly ValueConverter<List<string>?, string?> NullableStringListConverter = new(
        v => v == null ? null : JsonSerializer.Serialize(v, Json),
        v => v == null ? null : JsonSerializer.Deserialize<List<string>>(v, Json));

    internal static readonly ValueComparer<List<string>?> NullableStringListComparer = new(
        (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
        v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        v => v == null ? null : v.ToList());

    public void Configure(EntityTypeBuilder<Roadmap> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.CandidateId).IsRequired();

        // BE-6 — NULL cho hàng tạo trước BE-6 (không backfill; đường đọc tự suy tên).
        // `MaxLength` khớp hằng số dùng chung `RoadmapNaming.MaxLength` — lệch giữa DB và tầng
        // validate thì người dùng gõ qua được ở API rồi bị DB từ chối, hoặc ngược lại.
        e.Property(x => x.Name)
            .HasColumnType("text")
            .HasMaxLength(RoadmapNaming.MaxLength);

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        e.Property(x => x.Level)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        e.Property(x => x.Language).HasColumnType("text").HasDefaultValue("vi").IsRequired();
        e.HasCheckConstraint("ck_roadmaps_language", "language IN ('vi', 'en')");

        // Chế độ lộ trình (LevelUp | Reinforce). Lưu STRING (GEN-2) + CHECK ở TẦNG DB, mẫu
        // `language` ngay trên: enum .NET chỉ chắn được đường đi qua code, còn CHECK chắn cả
        // đường ghi thẳng bằng SQL. Default 'LevelUp' phủ mọi hàng tạo trước cột này ⇒ migration
        // KHÔNG cần backfill, và hàng cũ mang đúng ngữ nghĩa vốn có của chúng.
        e.Property(x => x.Mode)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(RoadmapMode.LevelUp)
            .IsRequired();
        e.HasCheckConstraint("ck_roadmaps_mode", "mode IN ('LevelUp', 'Reinforce')");

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // jsonb? — inline null-check converter (mẫu CvAnalysis.JdMatch): null → SQL NULL.
        var sourceIds = e.Property(x => x.SourceSessionIds)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<List<Guid>>(v, Json))
            .HasColumnType("jsonb");
        sourceIds.Metadata.SetValueComparer(GuidListComparer);

        var baseline = e.Property(x => x.Baseline)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, decimal>>(v, Json))
            .HasColumnType("jsonb");
        baseline.Metadata.SetValueComparer(DecimalDictComparer);

        // Snapshot RoadmapReport khi Completed (BC15) — raw JSON, BC12 luôn null.
        e.Property(x => x.FinalReport).HasColumnType("jsonb");

        e.Property(x => x.OverallComment).HasColumnType("text");

        e.Property(x => x.CreatedAt).IsRequired();

        // Roadmap của 1 user (GET /roadmaps + lịch sử BC-3).
        e.HasIndex(x => x.CandidateId);

        // cv_id → file_records Restrict (chặn xoá CV đang gắn roadmap) — đồng bộ PracticeSession.CvId.
        e.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(x => x.CvId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasMany(x => x.Milestones)
            .WithOne(m => m.Roadmap)
            .HasForeignKey(m => m.RoadmapId)
            .OnDelete(DeleteBehavior.Cascade);

        // MIS1-B4 — Cascade theo roadmap_id (xoá roadmap → xoá luôn lỗi đã trích của nó).
        e.HasMany(x => x.Mistakes)
            .WithOne(m => m.Roadmap)
            .HasForeignKey(m => m.RoadmapId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RoadmapMilestoneConfiguration : IEntityTypeConfiguration<RoadmapMilestone>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<RoadmapMilestone> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.OrderNo).IsRequired();
        e.Property(x => x.Title).HasMaxLength(256).IsRequired();

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // jsonb string[] non-null (converter + comparer, mẫu CvAnalysis.Strengths).
        var listConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>());

        var listComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        var focus = e.Property(x => x.FocusCriteria);
        focus.HasConversion(listConverter);
        focus.Metadata.SetValueComparer(listComparer);
        focus.HasColumnType("jsonb");
        focus.IsRequired();

        // jsonb? — { criterionName: deltaPct }; BC12 null, set khi Completed (BC15).
        var improvement = e.Property(x => x.Improvement)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, decimal>>(v, Json))
            .HasColumnType("jsonb");
        improvement.Metadata.SetValueComparer(RoadmapConfiguration.DecimalDictComparer);

        // jsonb? — phần TÍNH đã chốt của chặng (xem MilestoneScoreSnapshot). Converter null-safe
        // (mẫu RoadmapLesson.GroundingRefs) ⇒ hàng cũ giữ SQL NULL và migration KHỎI `defaultValue`.
        // ⚠ `defaultValue: ""` cho cột jsonb là lỗi migration mà test .NET KHÔNG bắt được: chuỗi rỗng
        // không phải JSON hợp lệ nên Postgres từ chối ngay tại ALTER TABLE, trong khi SQLite
        // (EnsureCreated) bỏ qua migration nên xanh 100% (tiền lệ F15).
        var snapshotConverter = new ValueConverter<MilestoneScoreSnapshot?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, Json),
            v => v == null ? null : JsonSerializer.Deserialize<MilestoneScoreSnapshot>(v, Json));
        var snapshot = e.Property(x => x.ScoreSnapshot);
        snapshot.HasConversion(snapshotConverter);
        // So sánh bằng chính JSON đã serialize: record `with` các List lồng nhau nên so tham chiếu
        // sẽ báo "đã đổi" mỗi lần load ⇒ ghi thừa; so cấu trúc bằng tay thì phải bảo trì 3 record.
        snapshot.Metadata.SetValueComparer(new ValueComparer<MilestoneScoreSnapshot?>(
            (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
            v => v == null ? 0 : JsonSerializer.Serialize(v, Json).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<MilestoneScoreSnapshot>(
                JsonSerializer.Serialize(v, Json), Json)));
        snapshot.HasColumnType("jsonb");

        // MIS1-B4 — mistake_key AI gom vào chặng này. jsonb? — mẫu ScoreSnapshot/GroundingRefs
        // (converter null-safe, KHÔNG defaultValue).
        var mistakeRefs = e.Property(x => x.MistakeRefs);
        mistakeRefs.HasConversion(RoadmapConfiguration.NullableStringListConverter);
        mistakeRefs.Metadata.SetValueComparer(RoadmapConfiguration.NullableStringListComparer);
        mistakeRefs.HasColumnType("jsonb");

        // UNIQUE(roadmap_id, order_no).
        e.HasIndex(x => new { x.RoadmapId, x.OrderNo }).IsUnique();

        e.HasMany(x => x.Lessons)
            .WithOne(l => l.Milestone)
            .HasForeignKey(l => l.MilestoneId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RoadmapLessonConfiguration : IEntityTypeConfiguration<RoadmapLesson>
{
    public void Configure(EntityTypeBuilder<RoadmapLesson> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.OrderNo).IsRequired();
        e.Property(x => x.Title).HasMaxLength(256).IsRequired();
        e.Property(x => x.TheoryContent).HasColumnType("text");

        // F15 — tài liệu học gợi ý: jsonb non-null (mặc định []). Converter → JSON string nên
        // SQLite (test) serialize ra text == Postgres jsonb (mẫu RubricLevel.ExampleAnswers/DB15).
        var resourceConverter = new ValueConverter<List<LessonResource>, string>(
            v => JsonSerializer.Serialize(v ?? new List<LessonResource>(), RoadmapConfiguration.LessonJson),
            v => JsonSerializer.Deserialize<List<LessonResource>>(v, RoadmapConfiguration.LessonJson)
                 ?? new List<LessonResource>());

        var resourceComparer = new ValueComparer<List<LessonResource>>(
            (a, b) => (a ?? new List<LessonResource>()).SequenceEqual(b ?? new List<LessonResource>()),
            v => v == null ? 0 : v.Aggregate(0, (h, r) => HashCode.Combine(h, r.GetHashCode())),
            v => v.ToList());

        var resources = e.Property(x => x.Resources);
        resources.HasConversion(resourceConverter);
        resources.Metadata.SetValueComparer(resourceComparer);
        resources.HasColumnType("jsonb");
        resources.IsRequired();

        // RAG grounding (Cách 2) — snapshot chunk precompute lúc tạo roadmap (jsonb NULLABLE — 3 trạng thái).
        // Content cần để feed /generate-lesson-theory lúc mở lesson mà KHÔNG retrieve lại. Null-safe converter
        // (mẫu SourceSessionIds) ⇒ row cũ NULL, migration khỏi defaultValue → né bug jsonb-rỗng F15.
        var groundingConverter = new ValueConverter<List<GroundingChunk>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, KnowledgeJson.Options),
            v => v == null ? null : JsonSerializer.Deserialize<List<GroundingChunk>>(v, KnowledgeJson.Options));
        var groundingComparer = new ValueComparer<List<GroundingChunk>?>(
            (a, b) => (a ?? new List<GroundingChunk>()).SequenceEqual(b ?? new List<GroundingChunk>()),
            v => v == null ? 0 : v.Aggregate(0, (h, r) => HashCode.Combine(h, r.GetHashCode())),
            v => v == null ? null : v.ToList());
        var grounding = e.Property(x => x.GroundingRefs);
        grounding.HasConversion(groundingConverter);
        grounding.Metadata.SetValueComparer(groundingComparer);
        grounding.HasColumnType("jsonb");

        // MIS1-B4 — mistake_key lesson này bám riêng. jsonb? — mẫu GroundingRefs (converter chung
        // RoadmapConfiguration.NullableStringListConverter, cùng type List<string>? với milestone).
        var lessonMistakeRefs = e.Property(x => x.MistakeRefs);
        lessonMistakeRefs.HasConversion(RoadmapConfiguration.NullableStringListConverter);
        lessonMistakeRefs.Metadata.SetValueComparer(RoadmapConfiguration.NullableStringListComparer);
        lessonMistakeRefs.HasColumnType("jsonb");

        // MIS1-B4 — "vì sao sai / sửa sao" (MIS1-B3 mistakeReview), sinh CÙNG lượt TheoryContent.
        // jsonb? — LessonMistakeReviewItem là record toàn string (immutable) nên SequenceEqual +
        // v.ToList() đã là deep-clone thật, khỏi cần khuôn riêng như MilestoneScoreSnapshot.
        var mistakeReviewConverter = new ValueConverter<List<LessonMistakeReviewItem>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, RoadmapConfiguration.LessonJson),
            v => v == null ? null
                : JsonSerializer.Deserialize<List<LessonMistakeReviewItem>>(v, RoadmapConfiguration.LessonJson));
        var mistakeReviewComparer = new ValueComparer<List<LessonMistakeReviewItem>?>(
            (a, b) => (a ?? new List<LessonMistakeReviewItem>())
                .SequenceEqual(b ?? new List<LessonMistakeReviewItem>()),
            v => v == null ? 0 : v.Aggregate(0, (h, r) => HashCode.Combine(h, r.GetHashCode())),
            v => v == null ? null : v.ToList());
        var mistakeReview = e.Property(x => x.MistakeReview);
        mistakeReview.HasConversion(mistakeReviewConverter);
        mistakeReview.Metadata.SetValueComparer(mistakeReviewComparer);
        mistakeReview.HasColumnType("jsonb");

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // UNIQUE(milestone_id, order_no).
        e.HasIndex(x => new { x.MilestoneId, x.OrderNo }).IsUnique();

        // session_id → practice_sessions Restrict (giữ lịch sử luyện; không xoá session đang gắn lesson).
        e.HasOne<PracticeSession>()
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasMany(x => x.Attempts)
            .WithOne(a => a.Lesson)
            .HasForeignKey(a => a.LessonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Lịch sử các lần làm một bài luyện (làm lại để nâng điểm). Xem <see cref="RoadmapLessonAttempt"/>.
/// </summary>
public class RoadmapLessonAttemptConfiguration : IEntityTypeConfiguration<RoadmapLessonAttempt>
{
    public void Configure(EntityTypeBuilder<RoadmapLessonAttempt> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.AttemptNo).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // UNIQUE(lesson_id, attempt_no) — lá chắn TẦNG DB cho việc cấp số thứ tự. Số được tính bằng
        // `count + 1` SAU khi đã thắng cú lật trạng thái của lesson, nên về logic chỉ một request tới
        // được đây; ràng buộc này để nếu giả định đó sai thì vỡ TO chứ không cấp trùng số im lặng.
        e.HasIndex(x => new { x.LessonId, x.AttemptNo }).IsUnique();

        // UNIQUE(session_id) — 1 buổi luyện thuộc đúng 1 lần làm. Cũng là thứ giữ cho báo cáo tiến
        // độ không đếm một buổi hai lần khi hợp hai nguồn (xem RoadmapReportService).
        e.HasIndex(x => x.SessionId).IsUnique();

        // session_id → practice_sessions Restrict — cùng ràng buộc như roadmap_lessons.session_id.
        e.HasOne<PracticeSession>()
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// MIS1-B4 — bảng CON thay vì jsonb trên <c>roadmaps</c> (xem lý do TOAST ở
/// <see cref="RoadmapMistake"/>). UNIQUE(roadmap_id, mistake_key) ép "mint 1 lần" ở TẦNG DB.
/// 3 CHECK <c>jsonb_typeof(...)='array'</c> cho <c>mistake_refs</c>/<c>mistake_review</c> (2 bảng
/// trên) nằm ở MIGRATION (raw SQL cuối <c>Up()</c>), KHÔNG ở đây — <c>HasCheckConstraint</c> sẽ
/// nhúng thẳng vào <c>CREATE TABLE</c> cho MỌI provider kể cả SQLite, mà SQLite không có
/// <c>jsonb_typeof</c> ⇒ nổ ngay lúc <c>TestDb</c> khởi tạo schema (F15 tiền lệ).
/// </summary>
public class RoadmapMistakeConfiguration : IEntityTypeConfiguration<RoadmapMistake>
{
    public void Configure(EntityTypeBuilder<RoadmapMistake> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.MistakeKey).HasMaxLength(8).IsRequired();
        e.Property(x => x.CriterionName).HasMaxLength(256).IsRequired();
        e.Property(x => x.Question).HasColumnType("text").IsRequired();
        e.Property(x => x.Answer).HasColumnType("text").IsRequired();
        e.Property(x => x.Reasoning).HasColumnType("text").IsRequired();
        e.Property(x => x.SampleAnswer).HasColumnType("text");

        // REC1-B2 mục B — snapshot trình độ lúc trích, NULLABLE (hàng cũ không có, xem entity).
        // CHECK cùng tập giá trị với `ck_practice_sessions_seniority`/`Seniority` enum — NULL vẫn
        // qua CHECK bình thường (Postgres: NULL trong IN(...) không vi phạm) nên additive an toàn.
        e.Property(x => x.Seniority).HasMaxLength(16);
        e.HasCheckConstraint("ck_roadmap_mistakes_seniority",
            "seniority IS NULL OR seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");

        // numeric(5,2) — nguồn là phép chia; "lưu đủ" (làm tròn lúc GỬI ở B5, không phải lúc LƯU).
        e.Property(x => x.ScorePct).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.ThresholdPct).HasColumnType("numeric(5,2)").IsRequired();

        e.Property(x => x.CreatedAt).IsRequired();

        // UNIQUE(roadmap_id, mistake_key) — DB-enforce "mint 1 lần, không re-derive từ index".
        e.HasIndex(x => new { x.RoadmapId, x.MistakeKey }).IsUnique();

        // criterion_id → rubric_criteria Restrict — cùng khuôn AnswerScore.CriterionId /
        // SessionCriterionScore.CriterionId (đều Restrict).
        e.HasOne(x => x.Criterion)
            .WithMany()
            .HasForeignKey(x => x.CriterionId)
            .OnDelete(DeleteBehavior.Restrict);

        // answer_id → practice_answers SetNull (KHÔNG navigation — mẫu Roadmap.CvId /
        // RoadmapLesson.SessionId). Xoá answer/session gốc không được sập roadmap đã tạo trước đó;
        // hàng lỗi tự mang đủ snapshot Question/Answer/Reasoning để sống thiếu con trỏ này.
        e.HasOne<PracticeAnswer>()
            .WithMany()
            .HasForeignKey(x => x.AnswerId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
