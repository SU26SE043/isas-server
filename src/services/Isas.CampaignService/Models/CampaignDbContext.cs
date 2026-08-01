using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<CampaignInvitation> CampaignInvitations => Set<CampaignInvitation>();
        public DbSet<CampaignRanking> CampaignRankings => Set<CampaignRanking>();
        public DbSet<CvSubmission> CvSubmissions => Set<CvSubmission>();                         // C13: sàng CV (DB16, ex campaign_candidates)
        public DbSet<CampaignMembership> CampaignMemberships => Set<CampaignMembership>();        // D2: membership ứng viên↔campaign (DB16)
        public DbSet<CandidateCriterionScore> CandidateCriterionScores => Set<CandidateCriterionScore>();
        public DbSet<SessionFlag> SessionFlags => Set<SessionFlag>();                            // SEC-1: cờ chống gian lận cho HR
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();                      // DB2b: transactional outbox (invitation-email)
        public DbSet<ApiKey> ApiKeys => Set<ApiKey>();                                           // F17: API key bên thứ ba (ATS), gắn theo org

        // C13: string[] ↔ JSON (jsonb trên Npgsql; text trên SQLite test). Portable — filter đọc/ghi trong C#,
        // không query trong JSON. Comparer để EF theo dõi thay đổi phần tử đúng (list là mutable reference).
        private static readonly ValueConverter<List<string>?, string?> StringListConverter = new(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => v == null ? null : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null));

        private static readonly ValueComparer<List<string>?> StringListComparer = new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v == null ? null : v.ToList());

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
                        "(max_follow_ups IS NULL OR max_follow_ups >= 0) AND (max_questions IS NULL OR max_questions >= 0)");
                    t.HasCheckConstraint("ck_campaigns_status", "status IN ('Draft', 'Active', 'Closed', 'Archived')");
                });
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.Title).HasMaxLength(255).IsRequired();
                e.Property(x => x.Domain).HasMaxLength(100);

                e.Property(x => x.Status)
                 .HasConversion<string>()
                 .HasMaxLength(20)
                 .HasDefaultValue(CampaignStatus.Draft);

                e.Property(x => x.AntiCheatEnabled).HasDefaultValue(true);
                e.Property(x => x.FaceVerifyEnabled).HasDefaultValue(false);   // SEC-1: face-verify opt-in (B2B)
                e.Property(x => x.AdaptiveEnabled).HasDefaultValue(false);     // INT-17: adaptive opt-in (B2B)
                e.Property(x => x.GroundingEnabled).HasDefaultValue(false);    // T8: entitlement-gated snapshot

                e.Property(x => x.StartsAt).IsRequired();

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // Soft delete (D11): mọi query tự lọc deleted_at IS NULL
                e.HasQueryFilter(x => x.DeletedAt == null);

                // C13: rule cứng sàng CV — string[] lưu jsonb (Npgsql) / text (SQLite) qua converter.
                e.Property(x => x.RequiredSkills).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.KeywordsAny).HasConversion(StringListConverter, StringListComparer);
                if (Database.IsNpgsql())
                {
                    e.Property(x => x.RequiredSkills).HasColumnType("jsonb");
                    e.Property(x => x.KeywordsAny).HasColumnType("jsonb");

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

            // ── CampaignQuestion ─────────────────────────────────────────
            modelBuilder.Entity<CampaignQuestion>(e =>
            {
                e.ToTable("campaign_questions", t => t.HasCheckConstraint(
                    "ck_campaign_questions_source", "source IN ('AiGenerated', 'CustomHr')"));
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.QuestionText).IsRequired();
                e.Property(x => x.IsRequired).HasDefaultValue(true);

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
                    t.HasCheckConstraint("ck_campaign_criteria_source", "source IN ('AiSuggested', 'HrEdited')");
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

            // ── AuditLog (vết thao tác — C10/D11) ───────────────────────────
            modelBuilder.Entity<AuditLog>(e =>
            {
                e.ToTable("audit_logs", t => t.HasCheckConstraint(
                    "ck_audit_logs_action", "action IN ('CreateCampaign', 'EditQuestions', 'EditCriteria', 'Publish', 'Delete', 'TransitionStatus', 'Invite', 'ScreenCandidates', 'EditCandidate', 'ReissueInvitation', 'OverrideResult', 'CreateApiKey', 'RevokeApiKey')"));
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

                e.Property(x => x.ParseStatus).HasConversion<string>().HasMaxLength(16);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.YearsExperience).HasColumnType("numeric(4,1)");

                e.Property(x => x.Skills).HasConversion(StringListConverter, StringListComparer);
                if (Database.IsNpgsql())
                    e.Property(x => x.Skills).HasColumnType("jsonb");

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
                    t.HasCheckConstraint("ck_campaign_membership_interview_status", "interview_status IS NULL OR interview_status IN ('NotStarted', 'InProgress', 'Completed')");
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
                e.ToTable("session_flags");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.SignalType).IsRequired().HasMaxLength(32);
                e.Property(x => x.DetectedAt).HasDefaultValueSql("now()");

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
        }
    }
}
