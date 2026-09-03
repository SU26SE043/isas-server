using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace Isas.CampaignService.Models
{
    public class CampaignDbContext : DbContext
    {
        public CampaignDbContext(DbContextOptions<CampaignDbContext> options) : base(options) { }

        public DbSet<Campaign> Campaigns => Set<Campaign>();
        public DbSet<CampaignQuestion> CampaignQuestions => Set<CampaignQuestion>();
        public DbSet<CampaignCriterion> CampaignCriteria => Set<CampaignCriterion>();
        public DbSet<CampaignCriterionLevel> CampaignCriterionLevels => Set<CampaignCriterionLevel>();   // CAMP-16/17: mốc điểm
        public DbSet<RubricPreviewRun> RubricPreviewRuns => Set<RubricPreviewRun>();                     // CAMP-19: chấm thử
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<CampaignInvitation> CampaignInvitations => Set<CampaignInvitation>();
        public DbSet<CampaignSlot> CampaignSlots => Set<CampaignSlot>();
        public DbSet<CampaignRanking> CampaignRankings => Set<CampaignRanking>();
        public DbSet<CvSubmission> CvSubmissions => Set<CvSubmission>();                         // C13: sàng CV (DB16, ex campaign_candidates)
        public DbSet<CampaignMembership> CampaignMemberships => Set<CampaignMembership>();        // D2: membership ứng viên↔campaign (DB16)
        public DbSet<CandidateCriterionScore> CandidateCriterionScores => Set<CandidateCriterionScore>();
        public DbSet<SessionFlag> SessionFlags => Set<SessionFlag>();                            // SEC-1: cờ chống gian lận cho HR
        public DbSet<FaceImage> FaceImages => Set<FaceImage>();                                  // BK25: sổ theo dõi ảnh sinh trắc trong S3 (DATA-3)
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();                      // DB2b: transactional outbox (invitation-email)
        public DbSet<ApiKey> ApiKeys => Set<ApiKey>();                                           // F17: API key bên thứ ba (ATS), gắn theo org
        public DbSet<ScoringPolicy> ScoringPolicies => Set<ScoringPolicy>();                     // SCP1: chính sách chấm điểm (biểu thức) + mẫu hệ thống

        // C13: string[] ↔ JSON (jsonb trên Npgsql; text trên SQLite test). Portable — filter đọc/ghi trong C#,
        // không query trong JSON. Comparer để EF theo dõi thay đổi phần tử đúng (list là mutable reference).
        private static readonly ValueConverter<List<string>?, string?> StringListConverter = new(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => v == null ? null : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null));

        private static readonly ValueComparer<List<string>?> StringListComparer = new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v == null ? null : v.ToList());

        // HR technical screener — list OBJECT ↔ JSON (jsonb Npgsql / text SQLite), cùng nguyên tắc
        // portable như StringListConverter: đọc/ghi cả cục trong C#, KHÔNG query vào trong JSON.
        //
        // ⚠ So sánh bằng chuỗi JSON đã serialize chứ không so từng field: object không override
        // Equals nên SequenceEqual chỉ so tham chiếu ⇒ EF sẽ coi "sửa Level của một phần tử" là
        // KHÔNG đổi và bỏ qua lúc SaveChanges — sai lặng lẽ, không lỗi gì.
        private static ValueConverter<List<T>?, string?> JsonListConverter<T>() => new(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => v == null ? null : JsonSerializer.Deserialize<List<T>>(v, (JsonSerializerOptions?)null));

        private static ValueComparer<List<T>?> JsonListComparer<T>() => new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                   == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<List<T>>(
                JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

        // SCP1 · B5 — cùng nguyên tắc JsonListConverter nhưng cho MỘT object (không phải List): dùng
        // cho campaign_rankings.scoring_inputs (ScoringInputsSnapshot). So sánh bằng chuỗi JSON đã
        // serialize (record snapshot bất biến sau khi ghi, nhưng vẫn giữ để EF không coi mọi lần load
        // là "đã đổi").
        private static ValueConverter<T?, string?> JsonObjectConverter<T>() where T : class => new(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => v == null ? null : JsonSerializer.Deserialize<T>(v, (JsonSerializerOptions?)null));

        private static ValueComparer<T?> JsonObjectComparer<T>() where T : class => new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                   == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── Campaign ──────────────────────────────────────────────────
            modelBuilder.Entity<Campaign>(e =>
            {
                // DB15: pass_score_pct là % điểm tổng để auto pass/fail (E5) → phải NULL (HR quyết tay)
                // hoặc ∈ [0,100]. CHECK ở tầng DB khớp guard code ValidatePassScorePct (CampaignService.cs).
                e.ToTable("campaigns", t =>
                {
                    t.HasCheckConstraint(
                        "ck_campaigns_pass_score_pct_range",
                        "pass_score_pct IS NULL OR (pass_score_pct >= 0 AND pass_score_pct <= 100)");
                    // INT-17: trần câu hỏi thích ứng phải null (dùng mặc định Interview) hoặc KHÔNG âm.
                    // Khớp guard code ValidateAdaptiveCaps (CampaignService.cs).
                    t.HasCheckConstraint(
                        "ck_campaigns_adaptive_caps_non_negative",
                        "(max_follow_ups IS NULL OR max_follow_ups >= 0) AND (max_questions IS NULL OR max_questions >= 0) AND (max_deep_per_question IS NULL OR max_deep_per_question >= 0)");
                    // NGÂN HÀNG ĐỀ: null = lấy hết câu (hành vi cũ). Đặt số thì phải ≥ 1 — `0` nghĩa là
                    // "buổi thi không câu nào", mà `ParticipationService` đã ném khi đề rỗng ⇒ để lọt là
                    // tạo ra campaign publish được nhưng KHÔNG ứng viên nào bắt đầu nổi.
                    t.HasCheckConstraint(
                        "ck_campaigns_questions_per_session_positive",
                        "questions_per_session IS NULL OR questions_per_session >= 1");
                    // CAMP-18: định danh bộ thước đo. Bắt đầu từ 1 và chỉ tăng — số 0/âm nghĩa là
                    // có đường ghi nào đó đang đặt bừa, mà nhãn thước đo sai thì bảng xếp hạng trộn
                    // hai nhóm điểm không so sánh được (CAMP-10) mà không ai thấy.
                    t.HasCheckConstraint("ck_campaigns_rubric_version_positive", "rubric_version >= 1");
                    t.HasCheckConstraint("ck_campaigns_status", "status IN ('Draft', 'Active', 'Closed', 'Archived')");
                    t.HasCheckConstraint("ck_campaigns_language", "language IN ('vi', 'en')");
                    t.HasCheckConstraint("ck_campaigns_seniority", "seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.Title).HasMaxLength(255).IsRequired();
                e.Property(x => x.Domain).HasMaxLength(100);
                e.Property(x => x.Language).HasColumnType("text").IsRequired().HasDefaultValue("vi");
                e.Property(x => x.Seniority).HasMaxLength(16).IsRequired().HasDefaultValue("Junior");

                e.Property(x => x.Status)
                 .HasConversion<string>()
                 .HasMaxLength(20)
                 .HasDefaultValue(CampaignStatus.Draft);

                e.Property(x => x.AntiCheatEnabled).HasDefaultValue(true);
                e.Property(x => x.FaceVerifyEnabled).HasDefaultValue(false);   // SEC-1: face-verify opt-in (B2B)
                // RNK1 · HĐ-2 / CAMP-21 — LUẬT câu bỏ trống. DEFAULT true = campaign tạo TỪ bản này bị
                // phạt. Campaign đã có TRƯỚC bản này: migration AddColumn(defaultValue: true) rồi
                // UPDATE campaigns SET skip_penalty = false ⇒ chúng KHÔNG bị đổi thước đo giữa chừng.
                e.Property(x => x.SkipPenalty).HasDefaultValue(true);
                e.Property(x => x.AdaptiveEnabled).HasDefaultValue(false);     // INT-17: adaptive opt-in (B2B)
                e.Property(x => x.GroundingEnabled).HasDefaultValue(false);    // T8: entitlement-gated snapshot
                // CAMP-18 — DEFAULT 1 để campaign đã có trên prod nhận đúng v1 mà không cần backfill:
                // mọi lượt materialize từng chạy đều ghi Version = 1 phía Interview.
                e.Property(x => x.RubricVersion).HasDefaultValue(1);

                e.Property(x => x.StartsAt).IsRequired();

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // Soft delete (D11): mọi query tự lọc deleted_at IS NULL
                e.HasQueryFilter(x => x.DeletedAt == null);

                // C13: rule cứng sàng CV — string[] lưu jsonb (Npgsql) / text (SQLite) qua converter.
                e.Property(x => x.RequiredSkills).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.KeywordsAny).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.JobNeeds)
                 .HasConversion(JsonListConverter<JobNeed>(), JsonListComparer<JobNeed>());
                if (Database.IsNpgsql())
                {
                    e.Property(x => x.RequiredSkills).HasColumnType("jsonb");
                    e.Property(x => x.KeywordsAny).HasColumnType("jsonb");
                    e.Property(x => x.JobNeeds).HasColumnType("jsonb");

                    // DB10: optimistic concurrency qua system column Postgres `xmin` — KHÔNG thêm DDL
                    // (map cột hệ thống sẵn có làm concurrency token; migration generator bỏ qua xmin).
                    // Npgsql 10 gỡ shorthand `UseXminAsConcurrencyToken()` → khai tường minh shadow
                    // property (đúng implementation cũ của shorthand). Gated IsNpgsql (mirror jsonb):
                    // SQLite test không có xmin → không map, không phá EnsureCreated.
                    e.Property<uint>("xmin")
                     .HasColumnName("xmin")
                     .HasColumnType("xid")
                     .ValueGeneratedOnAddOrUpdate()
                     .IsConcurrencyToken();
                }

                // Indexes — lọc theo owner ORG (BK4)
                e.HasIndex(x => new { x.OrgId, x.Status });
                // DB26/DB31: đuôi `Id` để phủ TRỌN khoá sắp xếp keyset của list Employer
                // (`GetCampaignsAsync`: WHERE org_id = @o ORDER BY created_at DESC, id DESC). Không có
                // `id` ở đuôi thì tie-break phải sort ở heap mỗi khi trùng created_at. Superset của
                // index cũ (org_id, created_at) → mọi truy vấn cũ vẫn được phục vụ y nguyên.
                e.HasIndex(x => new { x.OrgId, x.CreatedAt, x.Id });
            });

            modelBuilder.Entity<CampaignSlot>(e =>
            {
                e.ToTable("campaign_slots", t =>
                {
                    t.HasCheckConstraint("ck_campaign_slots_range", "ends_at > starts_at");
                    t.HasCheckConstraint("ck_campaign_slots_capacity", "capacity > 0");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.HasIndex(x => new { x.CampaignId, x.StartsAt }).IsUnique();
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);
                e.HasOne(x => x.Campaign).WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignQuestion ─────────────────────────────────────────
            modelBuilder.Entity<CampaignQuestion>(e =>
            {
                e.ToTable("campaign_questions", t => t.HasCheckConstraint(
                    "ck_campaign_questions_source", "source IN ('AiGenerated', 'CustomHr')"));
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.QuestionText).IsRequired();
                e.Property(x => x.IsRequired).HasDefaultValue(true);

                // Đáp án mẫu: cột `text` như `question_text`, KHÔNG HasMaxLength — vượt trần thì Postgres
                // ném lỗi thô không ai đọc được; trần độ dài chặn ở code (QuestionLimits) để trả 400 kèm
                // thông báo tiếng Việt. KHÔNG jsonb: cấu hình jsonb ở context này đều phải gate IsNpgsql()
                // vì SQLite của test không hỗ trợ.
                e.Property(x => x.SampleAnswer);
                e.Property(x => x.QuestionGroup).HasMaxLength(100);

                e.Property(x => x.Source)
                 .HasConversion<string>()
                 .HasMaxLength(20);

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                // DB13: khớp soft-delete filter của Campaign (required nav) → hết cảnh báo
                // PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning + không đọc con mồ côi.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Questions)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignCriterion (tiêu chí có cấu trúc — C8/D9) ─────────────
            modelBuilder.Entity<CampaignCriterion>(e =>
            {
                // DB15: weight ∈ (0,1] — khớp guard code BuildStructuredCriteria (0 < weight ≤ 1).
                e.ToTable("campaign_criteria", t =>
                {
                    t.HasCheckConstraint("ck_campaign_criteria_weight_range", "weight > 0 AND weight <= 1");
                    // EVA1-B3: max_score ∈ [1, 100] — khớp guard BuildStructuredCriteria. Không có
                    // cận trên thì thang 2147483647 làm TRÀN INT ở ScoringCriteriaBuilder ⇒ answer
                    // không bao giờ chấm ⇒ mất 1 credit im lặng (CAMP-17). Thang thật lớn nhất
                    // từng dùng là 30.
                    t.HasCheckConstraint("ck_campaign_criteria_max_score_range", "max_score >= 1 AND max_score <= 100");
                    // CAMP-20 — 'SystemDefault' là giá trị THỨ BA (bộ chuẩn chép về + bộ dự phòng khi AI
                    // lỗi). ⚠ CHECK này phải có trên DB TRƯỚC khi code ghi giá trị mới lên (xem docblock
                    // migration AddCriterionSourceSystemDefault). SQLite của test CÓ enforce CHECK (EF10)
                    // nhưng nó dựng schema bằng EnsureCreated theo model NÀY — tức luôn là bản ĐÃ nới —
                    // nên không test nào bắt được thứ tự deploy sai; chỉ Postgres thật mới bắt.
                    t.HasCheckConstraint("ck_campaign_criteria_source", "source IN ('AiSuggested', 'HrEdited', 'SystemDefault')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Name).IsRequired().HasMaxLength(255);
                e.Property(x => x.Weight).HasColumnType("numeric(5,4)");
                e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");   // C12

                // C12: tiêu chí structured HR khai thẳng — chống trùng + giữ thứ tự hiển thị.
                e.HasIndex(x => new { x.CampaignId, x.OrderNo }).IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.Name }).IsUnique();

                // DB13: khớp soft-delete filter của Campaign (required nav).
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Criteria)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignCriterionLevel (mốc điểm — CAMP-16/17, nuôi E9 hard-anchor) ──
            modelBuilder.Entity<CampaignCriterionLevel>(e =>
            {
                e.ToTable("campaign_criterion_levels", t =>
                {
                    // Điểm âm không có nghĩa ở bất kỳ thang nào; trần trên phụ thuộc criterion.max_score
                    // (không tham chiếu chéo bảng được trong CHECK) nên kiểm ở code — ValidateCriterionLevels.
                    t.HasCheckConstraint("ck_campaign_criterion_levels_score_non_negative", "score >= 0");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Descriptor).IsRequired();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // LÝ DO TỒN TẠI của bảng con thay vì jsonb: hai mốc trùng score làm việc snap điểm về
                // mức gần nhất (E9, cả Python lẫn C#) trở nên không xác định ⇒ chấm sai trong im lặng.
                e.HasIndex(x => new { x.CriterionId, x.Score }).IsUnique();

                // DB13: chained qua Criterion→Campaign (soft-delete filter). Bắt buộc khớp filter của
                // required nav, nếu không EF phát PossibleIncorrectRequiredNavigation + đọc mốc mồ côi.
                e.HasQueryFilter(x => x.Criterion.Campaign.DeletedAt == null);

                e.HasOne(x => x.Criterion)
                 .WithMany(x => x.Levels)
                 .HasForeignKey(x => x.CriterionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── RubricPreviewRun (chấm thử — CAMP-19) ────────────────────────
            modelBuilder.Entity<RubricPreviewRun>(e =>
            {
                e.ToTable("rubric_preview_runs", t => t.HasCheckConstraint(
                    "ck_rubric_preview_runs_status", "status IN ('Running', 'Succeeded', 'Failed')"));
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.QuestionText).IsRequired();
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
                e.Property(x => x.RubricSnapshot).IsRequired();
                e.Property(x => x.RubricFingerprint).HasMaxLength(64).IsRequired();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                // jsonb CÓ ĐIỀU KIỆN IsNpgsql (khớp precedent campaigns.required_skills / outbox payload)
                // — SQLite test không có jsonb. Kiểu C# là string nên KHÔNG cần ValueConverter, và nhờ
                // thế cũng không có chỗ nào cho EF scaffold `defaultValue: ""` (chuỗi rỗng không phải
                // JSON hợp lệ ⇒ Postgres từ chối ngay tại ALTER/CREATE, mà SQLite thì nuốt).
                if (Database.IsNpgsql())
                {
                    e.Property(x => x.RubricSnapshot).HasColumnType("jsonb");
                    e.Property(x => x.Samples).HasColumnType("jsonb");
                }

                // Lịch sử đọc theo campaign, mới nhất trước.
                e.HasIndex(x => new { x.CampaignId, x.CreatedAt });

                // Chống double-click / hai tab: chỉ MỘT lượt đang chạy trên mỗi campaign. Partial vì
                // lượt đã xong thì không còn ràng buộc gì (một campaign có nhiều lượt trong lịch sử).
                e.HasIndex(x => x.CampaignId)
                 .HasDatabaseName("ux_rubric_preview_runs_running")
                 .HasFilter("status = 'Running'")
                 .IsUnique();

                // DB13: required nav → Campaign (soft-delete filter) → BẮT BUỘC khớp filter.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── AuditLog (vết thao tác — C10/D11) ───────────────────────────
            modelBuilder.Entity<AuditLog>(e =>
            {
                e.ToTable("audit_logs", t => t.HasCheckConstraint(
                    "ck_audit_logs_action", "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey', 'ApplyScoringPolicy')"));
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Entity).IsRequired().HasMaxLength(64);
                e.Property(x => x.Action).HasConversion<string>().HasMaxLength(32);
                e.Property(x => x.At).HasDefaultValueSql("now()");
                e.HasIndex(x => new { x.EntityId, x.At });

                // DB26 — audit đọc theo NGƯỜI/TỔ CHỨC, không chỉ theo entity. Bảng này tồn tại để
                // đối chất/kiện (D11) và ba câu hỏi audit tự nhiên là "org X đã làm gì", "user Y đã
                // làm gì", "ai Publish trong khoảng thời gian này" — cả ba đều seq scan với mỗi
                // index (entity_id, at) hiện có. `at` ở đuôi để lọc/sắp theo thời gian ngay trong
                // index. Chi phí ghi chấp nhận được: audit_logs append-only và CHỈ ghi khi HR
                // mutation (tạo/publish/mời/sàng CV/override) — không nằm trên đường nóng nào.
                e.HasIndex(x => new { x.OrgId, x.At });
                e.HasIndex(x => new { x.ActorUserId, x.At });
            });

            // ── CampaignInvitation (magic-link mời — D1, đường 1: mời thẳng email) ──
            modelBuilder.Entity<CampaignInvitation>(e =>
            {
                e.ToTable("campaign_invitations");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                // DB23 — lưu SHA-256(token) base64 (44 ký tự), không phải token thô. Giữ nguyên
                // varchar(128) (hash vừa thoải mái) → migration không đụng kiểu/độ dài cột.
                e.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
                e.Property(x => x.Email).IsRequired().HasMaxLength(255);
                e.Property(x => x.ExpiresAt).IsRequired();   // DB23 — token luôn có hạn
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                // UNIQUE giữ nguyên shape (1 row/token) — tra bằng hash vẫn là single-row probe.
                e.HasIndex(x => x.TokenHash).IsUnique();
                e.HasIndex(x => x.CampaignId);
                e.HasOne<CampaignSlot>().WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.SetNull);

                // DB13: khớp soft-delete filter của Campaign (required nav).
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Invitations)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);

                // DB9/DB16: FK nội-service invitation → cv_submission (đường-2 từ shortlist). Optional
                // (campaign_candidate_id nullable; đường-1 mời-thẳng = null). SetNull: xoá CV →
                // invitation giữ lại, chỉ mất link shortlist. Optional nav → KHÔNG cần query filter mới.
                // Cột DB giữ tên campaign_candidate_id (không rename cột); nav re-point về CvSubmission.
                e.HasOne(x => x.CvSubmission)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignCandidateId)
                 .HasConstraintName("fk_campaign_invitations_cv_submission_campaign_candidate_id")
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── CampaignRanking (read-model B2B — E4/D10, event SessionScored) ─────
            modelBuilder.Entity<CampaignRanking>(e =>
            {
                e.ToTable("campaign_rankings");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.TotalScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                // E11b — HR override (nullable; null = chưa chốt tay).
                e.Property(x => x.OverrideScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.OverrideResult).HasMaxLength(10);

                // SCP1 · B5 — bó biến RAW đến qua event. NULLABLE (CẤM #4 — không NOT NULL cho cột
                // đến qua event). jsonb (Npgsql) / text (SQLite) qua converter object đơn.
                e.Property(x => x.ScoringInputs)
                 .HasConversion(JsonObjectConverter<ScoringInputsSnapshot>(), JsonObjectComparer<ScoringInputsSnapshot>());
                if (Database.IsNpgsql())
                    e.Property(x => x.ScoringInputs).HasColumnType("jsonb");

                // SCP1 · B8 / HĐ-5 — nhãn chính sách chấm đã áp + cờ lùi an toàn (xem CampaignRanking).
                e.Property(x => x.PolicyName).HasMaxLength(255);
                e.Property(x => x.ScoreFallback).HasDefaultValue(false);

                // Idempotent upsert theo session_id: event tới 2 lần vẫn 1 row.
                e.HasIndex(x => x.SessionId).IsUnique();
                // DB26 — rút `total_score` khỏi đuôi index. `GetCampaignResultsAsync` (E5) chỉ
                // `WHERE campaign_id = @c` rồi sắp TRONG BỘ NHỚ theo `override_score ?? total_score`
                // (E11b) — không index nào phục vụ được biểu thức đó, và query lấy cả entity nên
                // cũng không index-only-scan được. `total_score` ở đuôi vì thế là cột chết: phình
                // index + mỗi upsert ranking (event SessionScored) phải sửa index dù chỉ đổi điểm.
                // Bỏ nó ra → update điểm không đụng index này nữa (mở đường HOT update) và index vẫn
                // phủ FK campaign_rankings.campaign_id → campaigns (DB9).
                // Đánh đổi: nếu sau này cần ORDER BY total_score Ở SQL (paginate ranking) thì phải
                // thêm lại — nhưng chỉ khi bỏ sort in-memory, mà sort in-memory là CHỦ Ý (E5: decimal
                // trên SQLite lưu TEXT nên ORDER BY ở SQL sai thứ tự số học).
                e.HasIndex(x => x.CampaignId);

                // DB13/DB9: required nav (CampaignId NOT NULL) tới Campaign có soft-delete filter →
                // BẮT BUỘC khớp filter, nếu không EF phát PossibleIncorrectRequiredNavigation warning +
                // đọc ranking mồ côi (campaign đã soft-delete). Đọc ranking join campaigns tự ẩn.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                // DB9: FK nội-service campaign_rankings.campaign_id → campaigns.id. Restrict (bảo vệ
                // read-model ranking; campaign vốn soft-delete nên cascade không kích hoạt). CandidateId/
                // SessionId = ref XUYÊN service → giữ Guid lỏng (GEN-2), KHÔNG FK.
                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── CvSubmission (sàng CV B2B — C13/D18; DB16 ex campaign_candidates) ───
            modelBuilder.Entity<CvSubmission>(e =>
            {
                e.ToTable("cv_submission", t =>
                {
                    t.HasCheckConstraint("ck_cv_submission_parse_status", "parse_status IN ('Pending', 'Done', 'Failed')");
                    t.HasCheckConstraint("ck_cv_submission_status", "status IN ('Pending', 'Filtered', 'Rejected', 'Analyzing', 'Analyzed', 'AnalysisFailed', 'Invited')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.FullName).HasMaxLength(255);
                e.Property(x => x.Email).HasMaxLength(255);
                // SCP1 · B7 — cờ lùi an toàn của điểm sàng CV. NOT NULL default false (ghi cùng
                // transaction với hàng ⇒ hàng cũ = false = "không lùi an toàn").
                e.Property(x => x.ScoreFallback).HasDefaultValue(false);

                e.Property(x => x.ParseStatus).HasConversion<string>().HasMaxLength(16);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.YearsExperience).HasColumnType("numeric(4,1)");

                e.Property(x => x.Skills).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.BonusSignals).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.VerifyQuestions).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.Strengths)
                 .HasConversion(JsonListConverter<NeedAssessment>(), JsonListComparer<NeedAssessment>());
                e.Property(x => x.Gaps)
                 .HasConversion(JsonListConverter<NeedAssessment>(), JsonListComparer<NeedAssessment>());
                // 10 = đủ cho "Medium" (6). Cột enum-string ⇒ EnumColumnLengthTests (S11) là guard
                // sẵn có cho lớp bug varchar-hẹp-hơn-giá-trị đã làm vỡ đường tiền một lần.
                e.Property(x => x.VerificationRisk).HasMaxLength(10);
                if (Database.IsNpgsql())
                {
                    e.Property(x => x.Skills).HasColumnType("jsonb");
                    e.Property(x => x.BonusSignals).HasColumnType("jsonb");
                    e.Property(x => x.VerifyQuestions).HasColumnType("jsonb");
                    e.Property(x => x.Strengths).HasColumnType("jsonb");
                    e.Property(x => x.Gaps).HasColumnType("jsonb");
                }

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // UNIQUE(campaign_id, email) — chống trùng ứng viên trong campaign. NULL distinct
                // (Postgres & SQLite) → nhiều CV không tách được email vẫn insert được.
                e.HasIndex(x => new { x.CampaignId, x.Email }).IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.Status });

                // DB5: index cho StuckScreeningRepublisher (C15) — sweeper quét mỗi 2' theo predicate
                // (Status, LastScreeningPublishedAt) KHÔNG có campaign_id → index (campaign_id, status)
                // ở trên vô dụng (leading col không khớp). Non-partial: cả 2 nhánh sweeper (Filtered+null,
                // Analyzing+not-null) đều key theo status (cột dẫn đầu, selective — chỉ Filtered/Analyzing
                // là hot); LastScreeningPublishedAt cột phụ để so mốc. Status lưu string (HasConversion)
                // → không cần filter literal. DB16 — đổi tên theo bảng mới (cv_submission).
                e.HasIndex(x => new { x.Status, x.LastScreeningPublishedAt })
                 .HasDatabaseName("ix_cv_submission_status_lsp");

                // DB13: khớp soft-delete filter của Campaign (required nav).
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.CvSubmissions)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignMembership (D2 join — DB16 tách khỏi bảng God) ─────────────
            modelBuilder.Entity<CampaignMembership>(e =>
            {
                e.ToTable("campaign_membership", t =>
                {
                    t.HasCheckConstraint("ck_campaign_membership_status", "status IN ('Joined')");
                    t.HasCheckConstraint("ck_campaign_membership_interview_status", "interview_status IS NULL OR interview_status IN ('NotStarted', 'InProgress', 'Abandoned', 'Completed')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).HasDefaultValue(MembershipStatus.Joined);
                // interview_status enum string (nullable = NotStarted).
                e.Property(x => x.InterviewStatus).HasConversion<string>().HasMaxLength(16);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // F5 — snapshot danh tính cho bảng kết quả + CSV (HR đọc được thay vì toàn UUID).
                // FX1 — độ dài nay THỰC SỰ khớp nguồn (comment cũ nói "khớp cv_submission" nhưng để
                // 320/256 trong khi cả `cv_submission.email` lẫn `campaign_invitations.email` đều là
                // varchar(255), `cv_submission.full_name` là varchar(255)). Giá trị ở đây CHỈ sao chép
                // từ 2 nguồn đó (ApplyIdentitySnapshot) nên không thể dài hơn 255 ⇒ thu về 255 an toàn
                // và biến ràng buộc thành sự thật thay vì lời chú thích sai.
                e.Property(x => x.FullName).HasMaxLength(255);
                e.Property(x => x.Email).HasMaxLength(255);

                // 1 membership / (campaign, candidate) — chống join 2 lần (D2 idempotent).
                e.HasIndex(x => new { x.CampaignId, x.CandidateId }).IsUnique();
                e.HasIndex(x => x.CandidateId);
                // 1 membership / CV shortlist (đường-2). NULL distinct → nhiều đường-1 (không CV) vẫn insert.
                e.HasIndex(x => x.CvSubmissionId).IsUnique();

                // DB26 — `RankingEventHandler.MarkMembershipCompletedAsync` tra membership theo
                // session_id trên MỌI event SessionScored (đường nóng: mỗi buổi phỏng vấn chấm xong
                // = 1 lần). Không index nào phủ session_id → seq scan toàn bảng mỗi event.
                // PARTIAL `WHERE session_id IS NOT NULL`: membership tạo lúc join với session_id =
                // NULL, chỉ điền khi bấm Start → phần lớn bảng là NULL và predicate `= @sid` không
                // bao giờ khớp NULL. Postgres suy ra được `session_id = @p` ⇒ `IS NOT NULL` nên vẫn
                // dùng index. Neo trên cột gần-như-bất-biến (set 1 lần ở Start) → không index churn.
                // KHÔNG unique: đây là task hiệu năng; ràng buộc 1-session-1-membership là thay đổi
                // ngữ nghĩa (và rủi ro fail lúc apply nếu dữ liệu cũ có trùng) → để riêng nếu cần.
                e.HasIndex(x => x.SessionId)
                 .HasFilter("session_id IS NOT NULL");
                e.HasIndex(x => x.CampaignId).HasFilter("interview_status = 'InProgress'");
                e.HasOne<CampaignSlot>().WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.SetNull);

                // DB13: required nav → Campaign (soft-delete filter) → BẮT BUỘC khớp filter.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);

                // FK nội-service membership.cv_submission_id → cv_submission.id. OPTIONAL (nullable,
                // đường-1 mời-thẳng = null) → SetNull: xoá CV chỉ mất link shortlist, membership giữ.
                // Optional nav → KHÔNG cần query filter (D2 đường-1 không có CV).
                e.HasOne(x => x.CvSubmission)
                 .WithMany()
                 .HasForeignKey(x => x.CvSubmissionId)
                 .OnDelete(DeleteBehavior.SetNull);

                // FX1 — quan hệ THẬT membership → invitation (DB16 tách bảng nhưng bỏ quên khoá này,
                // buộc GetInvitationsAsync phải ghép bằng email = suy đoán).
                // Index thường, KHÔNG unique: reissue (D4) + join lại có thể cho nhiều membership cùng
                // trỏ 1 lời mời trong dữ liệu lịch sử; ràng buộc 1-1 là thay đổi ngữ nghĩa và có thể fail
                // lúc apply → để riêng nếu thật sự cần (mẫu comment index session_id ở trên).
                // SetNull (không Restrict): invitation cascade-delete theo campaign, Restrict sẽ chặn
                // xoá campaign; và mất link chỉ làm mất khả năng ghép chính xác, không mất membership.
                e.HasIndex(x => x.InvitationId)
                 .HasFilter("invitation_id IS NOT NULL");

                e.HasOne(x => x.Invitation)
                 .WithMany()
                 .HasForeignKey(x => x.InvitationId)
                 .HasConstraintName("fk_campaign_membership_invitation_invitation_id")
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── CandidateCriterionScore (điểm khớp/tiêu chí — C14, bảng tạo ở C13) ──
            modelBuilder.Entity<CandidateCriterionScore>(e =>
            {
                e.ToTable("candidate_criterion_scores");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.MatchScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasIndex(x => new { x.CvSubmissionId, x.CriterionId }).IsUnique();

                // DB13: CvSubmission + CampaignCriterion (2 required nav bên dưới) đã có soft-delete
                // filter → phải khớp filter ở đây, nếu không EF phát lại warning + đọc điểm mồ côi.
                // Chained qua CvSubmission→Campaign (Criterion cùng campaign → 1 điều kiện là đủ).
                e.HasQueryFilter(x => x.CvSubmission.Campaign.DeletedAt == null);

                e.HasOne(x => x.CvSubmission)
                 .WithMany(x => x.CriterionScores)
                 .HasForeignKey(x => x.CvSubmissionId)
                 .HasConstraintName("fk_candidate_criterion_scores_cv_submission_cv_submission_id")
                 .OnDelete(DeleteBehavior.Cascade);

                // Restrict: chặn xoá tiêu chí còn điểm tham chiếu (TÁI DÙNG rubric campaign_criteria).
                e.HasOne(x => x.Criterion)
                 .WithMany()
                 .HasForeignKey(x => x.CriterionId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── SessionFlag (cờ chống gian lận cho HR — SEC-1/D13) ─────────────────
            modelBuilder.Entity<SessionFlag>(e =>
            {
                e.ToTable("session_flags", t =>
                {
                    // MON1-B1 — khoá miền enum-string ở tầng DB (khớp pattern ck_campaign_membership_interview_status).
                    t.HasCheckConstraint("ck_session_flags_source", "source IN ('Client', 'Server')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.SignalType).IsRequired().HasMaxLength(32);
                e.Property(x => x.DetectedAt).HasDefaultValueSql("now()");
                // MON1-B1 — enum→STRING (GEN-2). NOT NULL DEFAULT 'Client': cờ cũ + cờ client hiện tại
                // đều là 'Client'; chỉ sweeper server (B2/B3) ghi 'Server'. maxLength 16 (chỗ chừa cho
                // giá trị nguồn dài hơn về sau) — CampaignEnumColumnLengthTests khoá "đủ dài".
                e.Property(x => x.Source).HasConversion<string>().HasMaxLength(16)
                 .IsRequired().HasDefaultValue(FlagSource.Client);

                // Gom cờ theo buổi (surface cho HR + aggregate results). SessionId/CandidateId = ref
                // XUYÊN service → giữ Guid lỏng (GEN-2), KHÔNG FK.
                e.HasIndex(x => x.SessionId);
                e.HasIndex(x => new { x.CampaignId, x.SessionId });

                // DB13/DB9: required nav (CampaignId NOT NULL) tới Campaign có soft-delete filter →
                // BẮT BUỘC khớp filter, nếu không EF phát PossibleIncorrectRequiredNavigation warning +
                // đọc cờ mồ côi (campaign đã soft-delete). Đọc cờ join campaigns tự ẩn.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                // DB9: FK nội-service session_flags.campaign_id → campaigns.id. Restrict (bảo vệ cờ gian
                // lận; campaign vốn soft-delete nên cascade không kích hoạt).
                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── FaceImage (sổ theo dõi ảnh sinh trắc trong S3 — BK25/DATA-3) ──────
            // ⚠ CỐ Ý KHÔNG có nav/FK tới Campaign, KHÁC session_flags của DB9. Nav BẮT BUỘC tới
            // Campaign kéo theo query filter soft-delete (DB13, nếu không thì
            // PossibleIncorrectRequiredNavigation warning + test DB13 ném) — mà campaign đã
            // soft-delete chính là nhóm CẦN PURGE NHẤT, filter sẽ giấu đúng những dòng đó khỏi job
            // dọn và ảnh của campaign bị xoá sẽ nằm trong S3 vĩnh viễn. Đây là SỔ RETENTION, mọi
            // tham chiếu để Guid lỏng (GEN-2).
            modelBuilder.Entity<FaceImage>(e =>
            {
                e.ToTable("face_images", t =>
                {
                    t.HasCheckConstraint("ck_face_images_kind", "kind IN ('Live', 'Reference')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
                // Key S3 dài nhất hiện tại ~ "campaigns/{36}/sessions/{36}/face-live-{32}.jpeg" ≈ 110 ký tự;
                // 512 để dư cho prefix đổi về sau mà không phải migration lần nữa.
                e.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.CapturedAt).HasDefaultValueSql("now()");

                // 1 object S3 = 1 dòng. Nhờ UNIQUE này, enroll lại ĐÚNG cùng key (cùng đuôi file) chỉ
                // cập nhật CapturedAt thay vì đẻ dòng thứ hai trỏ cùng chỗ (DATA-2: 1 bản/ứng viên/campaign).
                e.HasIndex(x => x.StorageKey).IsUnique();

                // Đường quét DUY NHẤT của FaceImagePurger: `WHERE captured_at < cutoff ORDER BY captured_at`.
                e.HasIndex(x => x.CapturedAt);

                // Truy ngược "ảnh của buổi thi này" khi HR/ops cần đối chất trước lúc hết hạn giữ.
                e.HasIndex(x => new { x.CampaignId, x.SessionId });
            });

            // ── OutboxMessage (transactional outbox invitation-email — DB2b) ──────
            // Config INLINE (Campaign không có Configurations/ + ApplyConfigurationsFromAssembly). Cột
            // snake_case tự sinh (UseSnakeCaseNamingConvention ở Program.cs). Payload jsonb có ĐIỀU KIỆN
            // IsNpgsql (khớp precedent campaigns.required_skills) — SQLite test không set jsonb.
            modelBuilder.Entity<OutboxMessage>(e =>
            {
                e.ToTable("outbox_messages");
                e.HasKey(x => x.Id);

                e.Property(x => x.Type).HasMaxLength(64).IsRequired();
                e.Property(x => x.Payload).IsRequired();
                if (Database.IsNpgsql())
                    e.Property(x => x.Payload).HasColumnType("jsonb");

                e.Property(x => x.InvitationId).IsRequired();
                e.Property(x => x.CampaignId).IsRequired();
                e.Property(x => x.OccurredAt).IsRequired();
                e.Property(x => x.PublishedAt);
                e.Property(x => x.Attempts).IsRequired();

                // Dispatcher quét row chưa gửi (published_at IS NULL) → partial index chỉ index row còn tồn
                // đọng. Postgres + SQLite (>=3.8) đều hỗ trợ partial index; filter dùng tên cột snake_case
                // (khớp UseSnakeCaseNamingConvention — test cũng phải bật snake_case, DB2 precedent).
                e.HasIndex(x => x.PublishedAt)
                 .HasFilter("published_at IS NULL");
            });

            // ── ApiKey (F17) ──────────────────────────────────────────────
            modelBuilder.Entity<ApiKey>(e =>
            {
                e.ToTable("api_keys");
                e.HasKey(x => x.Id);

                e.Property(x => x.OrgId).IsRequired();
                e.Property(x => x.Name).HasMaxLength(100).IsRequired();

                // Hash SHA-256 base64 = 44 ký tự; 128 cho thoải mái nếu sau này đổi thuật toán.
                e.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
                e.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();

                e.Property(x => x.IncludePii).IsRequired();
                e.Property(x => x.CreatedByUserId).IsRequired();
                e.Property(x => x.CreatedAt).IsRequired();
                // DB23 — hạn NOT NULL: cột hạn nullable là đúng cách credential sống vĩnh viễn.
                e.Property(x => x.ExpiresAt).IsRequired();
                e.Property(x => x.LastUsedAt);
                e.Property(x => x.RevokedAt);

                // UNIQUE trên hash: (a) đường xác thực là single-row index probe, không scan bảng —
                // quan trọng vì đây là đường nóng của MỌI request bên thứ ba; (b) chặn 2 hàng cùng
                // hash (đụng độ chỉ có thể do lỗi lập trình, và nó sẽ làm việc "key này thuộc org
                // nào" thành không xác định).
                e.HasIndex(x => x.KeyHash).IsUnique();

                // Liệt kê/đếm key active theo org.
                e.HasIndex(x => new { x.OrgId, x.CreatedAt });
            });

            // ── ScoringPolicy (SCP1 · HĐ-3) ───────────────────────────────
            modelBuilder.Entity<ScoringPolicy>(e =>
            {
                e.ToTable("scoring_policies", t =>
                {
                    t.HasCheckConstraint("ck_scoring_policies_kind", "kind IN ('Interview', 'CvScreening')");
                    t.HasCheckConstraint("ck_scoring_policies_version", "version >= 1");
                    t.HasCheckConstraint(
                        "ck_scoring_policies_pass_score_pct",
                        "pass_score_pct IS NULL OR (pass_score_pct >= 0 AND pass_score_pct <= 100)");
                });
                e.HasKey(x => x.Id);

                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
                e.Property(x => x.Version).IsRequired();
                e.Property(x => x.EngineVersion).HasMaxLength(16).IsRequired();
                e.Property(x => x.Name).HasMaxLength(255).IsRequired();
                // text: trần độ dài biểu thức (ScoringLimits.MaxExpressionLength) ép lúc PHÂN TÍCH ở
                // B1/B3, không ở DB — một câu 1001 ký tự phải ra lỗi TOO_LONG có vị trí, không phải
                // bị Postgres cắt cụt.
                e.Property(x => x.Expression).IsRequired();
                e.Property(x => x.CreatedAt).IsRequired();

                // 🔴 HĐ-3 — sau INSERT chỉ name/description sửa được. EF ném InvalidOperationException
                // nếu SaveChanges thấy một trong các trường dưới bị đổi trên entity đã có trong DB.
                // Chốt ở TẦNG MODEL (chạy cả trên SQLite test) — không dựa vào kỷ luật của service.
                foreach (var p in new[]
                {
                    nameof(ScoringPolicy.CampaignId), nameof(ScoringPolicy.Kind), nameof(ScoringPolicy.Version),
                    nameof(ScoringPolicy.EngineVersion), nameof(ScoringPolicy.Expression),
                    nameof(ScoringPolicy.PassScorePct), nameof(ScoringPolicy.SourceTemplateId),
                    nameof(ScoringPolicy.CreatedAt), nameof(ScoringPolicy.CreatedBy),
                })
                    e.Property(p).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

                // HĐ-3 §2 — HAI partial unique RIÊNG. Postgres coi NULL là distinct ⇒ một UNIQUE chung
                // (campaign_id, kind, version) KHÔNG chặn được hai MẪU trùng nhau (campaign_id = NULL).
                //   · mẫu hệ thống : một bản / (kind, name)
                //   · bản campaign : một bản / (campaign_id, kind, version)
                e.HasIndex(x => new { x.Kind, x.Name })
                 .HasDatabaseName("ux_scoring_policies_template")
                 .HasFilter("campaign_id IS NULL")
                 .IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.Kind, x.Version })
                 .HasDatabaseName("ux_scoring_policies_campaign")
                 .HasFilter("campaign_id IS NOT NULL")
                 .IsUnique();

                // HĐ-3 §4 — 5 mẫu hệ thống (campaign_id = NULL). Xem ScoringPolicySeed.
                e.HasData(ScoringPolicySeed.Templates);
            });
        }
    }
}
