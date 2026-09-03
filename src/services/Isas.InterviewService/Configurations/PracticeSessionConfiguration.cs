using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Isas.InterviewService.Configurations;

public class PracticeSessionConfiguration : IEntityTypeConfiguration<PracticeSession>
{
    public void Configure(EntityTypeBuilder<PracticeSession> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.CandidateId).IsRequired();
        // CvId, JdId giờ optional — KHÔNG IsRequired.
        // (Guid? trong entity đã đủ báo nullable; bỏ IsRequired cho rõ ý.)

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();
        e.Property(x => x.Seniority).HasMaxLength(16).IsRequired().HasDefaultValue("Junior");

        e.Property(x => x.CreatedAt).IsRequired();

        // F2 — thời lượng mỗi câu do ứng viên chọn. defaultValue 120 để row CŨ tự nhận giá trị hợp lệ
        // lúc apply migration (khỏi backfill riêng) và khớp đúng hằng số 120 vốn hardcode trước đây.
        e.Property(x => x.TimeLimitSec).IsRequired().HasDefaultValue(120);
        e.Property(x => x.Language).HasColumnType("text").IsRequired().HasDefaultValue("vi");

        // F2b — trần cứng số câu ở tầng DB. Tầng service đã chặn 1..20 cho B2C, nhưng đường internal
        // (Campaign → /internal/sessions/campaign) không đi qua guard đó ⇒ chốt ở đây cho mọi đường ghi.
        // 0 = "không trần cứng" (luồng tĩnh / adaptive tắt) nên phải nằm trong khoảng hợp lệ.
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_practice_sessions_max_questions_range", "max_questions BETWEEN 0 AND 20");
            t.HasCheckConstraint("ck_practice_sessions_language", "language IN ('vi', 'en')");
            t.HasCheckConstraint("ck_practice_sessions_seniority", "seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
            t.HasCheckConstraint("ck_practice_sessions_status", "status IN ('GeneratingQuestions', 'Ready', 'InProgress', 'Completed', 'Scoring', 'Scored', 'Failed', 'SessionAbandoned')");
        });

        // INT-17b — trần đào sâu MỖI câu gốc + bộ đếm lỗi decide-next. default 0 ⇒ row CŨ tự nhận
        // "chế độ cũ, chưa lỗi lần nào" lúc apply migration (khỏi backfill riêng).
        e.Property(x => x.MaxDeepPerQuestion).IsRequired().HasDefaultValue(0);
        e.Property(x => x.AdaptiveFailures).IsRequired().HasDefaultValue(0);

        // DB14 — audit updated_at: default now() ở DB (Postgres); C# init ở entity đảm nhận insert
        // (SQLite/EnsureCreated không có now()). Stamp tự động khi Modified qua SaveChanges override.
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // BC9 — điểm tổng buổi B2C (nullable; set khi Scored).
        e.Property(x => x.OverallScore).HasColumnType("numeric(5,2)");

        // Con dấu phạm vi chấm — NULLABLE và CỐ Ý KHÔNG có default: default sẽ gán một giá trị "đã
        // biết" cho toàn bộ row cũ, tức khai rằng ta biết chúng được chấm theo phạm vi nào. Ta không
        // biết (BK23). null phải giữ đúng nghĩa "không biết".
        e.Property(x => x.ScoringScopeVersion);

        // Ghim phiên bản rubric campaign — nullable, KHÔNG default: B2C không có rubric campaign nên
        // null ở đó là đúng nghĩa "không áp dụng". Buổi B2B cũ được backfill = 1 trong migration
        // (giá trị đã biết chắc, xem ghi chú ở entity), không phải bằng DB default.
        e.Property(x => x.CampaignRubricVersion);

        // SCP1 · B5 — ghim hợp đồng chấm điểm (chính sách biểu thức) của buổi B2B. 4 cột NULLABLE,
        // KHÔNG default: null = B2C / B2B chưa áp chính sách / buổi trước cột này (xem entity). Ghim
        // CẢ biểu thức vì Interview không đọc được bảng scoring_policies của Campaign lúc chấm.
        e.Property(x => x.CampaignPolicyVersion);
        e.Property(x => x.CampaignPolicyExpression).HasColumnType("text");
        e.Property(x => x.CampaignPolicyPassScorePct);
        e.Property(x => x.CampaignPolicyEngineVersion).HasMaxLength(16);

        // RNK1 · HĐ-2 / CAMP-21 — luật câu bỏ trống, ghim lúc tạo buổi. Required + default false ⇒
        // row cũ + B2C tự nhận "không phạt" ngay lúc AddColumn (khỏi backfill riêng). Campaign gửi
        // giá trị thật (campaigns.skip_penalty) qua CreateCampaignSessionInternalRequest.
        e.Property(x => x.SkipPenalty).IsRequired().HasDefaultValue(false);

        // BC10 — nhận xét chung buổi (AI sinh, nullable; set best-effort khi Scored). text (không giới hạn).
        e.Property(x => x.OverallComment).HasColumnType("text");

        // TOP1-B5 — danh mục đề tài GẮN cho buổi (jsonb NULLABLE — mẫu TargetCriterionIds/GroundingRefs
        // ngay trên/dưới: converter null-safe, NULLABLE nên `AddColumn` KHÔNG cần defaultValue → né hẳn
        // bug jsonb-rỗng-default F15 (Postgres từ chối tại ALTER TABLE, SQLite/EnsureCreated bỏ qua
        // migration nên test xanh 100% mà không phát hiện được).
        var topicsConverter = new ValueConverter<List<SessionTopic>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, KnowledgeJson.Options),
            v => v == null ? null : JsonSerializer.Deserialize<List<SessionTopic>>(v, KnowledgeJson.Options));
        var topicsComparer = new ValueComparer<List<SessionTopic>?>(
            (a, b) => (a ?? new List<SessionTopic>()).SequenceEqual(b ?? new List<SessionTopic>()),
            v => v == null ? 0 : v.Aggregate(0, (h, t) => HashCode.Combine(h, t.GetHashCode())),
            v => v == null ? null : v.ToList());
        var topics = e.Property(x => x.Topics);
        topics.HasConversion(topicsConverter);
        topics.Metadata.SetValueComparer(topicsComparer);
        topics.HasColumnType("jsonb");

        // B2B: lookup session theo campaign (S3/S4). Non-unique, nullable.
        e.HasIndex(x => x.CampaignId);

        // Capacity: đếm đúng tập nóng đang chiếm chỗ; filter phải khớp EnsureCapacityAsync.
        e.HasIndex(x => x.Status)
            .HasDatabaseName("ix_practice_sessions_running_capacity")
            .HasFilter("status IN ('GeneratingQuestions', 'Ready', 'InProgress')");

        // DB31 — lịch sử buổi luyện của 1 candidate, phân trang keyset `(created_at DESC, id DESC)`
        // (quy ước DB8). Composite (candidate_id, created_at DESC, id DESC) khớp ĐÚNG hình truy vấn
        // `WHERE candidate_id = @c ORDER BY created_at DESC, id DESC LIMIT n` → index-only range scan,
        // không sort. candidate_id lọc bằng '=' nên hướng của nó không quan trọng; 2 cột đuôi DESC khớp
        // ORDER BY (thứ tự trộn ASC/DESC không phục vụ được bằng backward-scan nên phải khai DESC thật).
        //
        // ⚠ Index single-col `ix_practice_sessions_candidate_id` CŨ đã BỎ: nó là tiền tố trái của composite
        // này ⇒ mọi lookup `candidate_id = @c` vẫn dùng được composite. candidate_id là Guid lỏng xuyên
        // service (GEN-2), KHÔNG có FK ⇒ không dính bài học DB5 (partial index trùng prefix FK convention
        // index khiến model-differ đòi drop index FK) — ở đây không có FK nào để bảo vệ.
        e.HasIndex(x => new { x.CandidateId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_practice_sessions_candidate_history")
            .IsDescending(false, true, true);

        // DB5 (đặt nền) + DB27 (RE-SHAPE) — index cho SessionAbandonSweeper (quét mỗi 2 phút).
        //
        // VÌ SAO ĐẢO ĐÁNH ĐỔI CỦA DB5: bản DB5 neo cột BẤT BIẾN (deadline / created_at) để tránh index
        // churn khi status đổi. Nhưng cột bất biến ở đây lại KHÔNG chọn lọc: `created_at < now()-2h` khớp
        // gần như MỌI session B2C từng tạo, `deadline < now()` khớp mọi campaign đã hết hạn — nên chi phí
        // quét tăng TUYẾN TÍNH theo toàn bộ lịch sử, mỗi 2 phút, vĩnh viễn. Cột chọn lọc thật (`status`,
        // tập nóng nhỏ) lại nằm ngoài index. Đưa status vào FILTER: churn chỉ O(5) lần/session trên tập
        // nóng, rẻ hơn nhiều so với quét toàn lịch sử. Mẫu đúng đã có sẵn: ix_practice_answers_status_lsp.
        //
        // ⚠ Partial index chỉ dùng được nếu planner CHỨNG MINH được predicate query ⇒ predicate index.
        // Đã verify bằng ToQueryString (Npgsql): EF render enum status thành LITERAL, không phải tham số
        // — `p.status = 'InProgress'` / `p.status IN ('Ready', 'InProgress')` — nên filter dưới đây khớp
        // đúng mệnh đề query và implication là hiển nhiên. (Nếu status bị đổi sang so bằng biến/tham số
        // thì partial index NGỪNG được dùng — xem test SweeperIndexTests khoá hợp đồng này.)
        //
        // (1) B2B quá hạn nhận bài — ScanExpiredB2BAsync:
        //     status IN (Ready, InProgress) && deadline != null && deadline < now. B2B Ready đã
        //     reserve credit tại Start, nên phải được sweeper dọn nếu đóng tab trước answer đầu tiên.
        e.HasIndex(x => x.Deadline)
            .HasDatabaseName("ix_practice_sessions_deadline")
            .HasFilter("status IN ('Ready', 'InProgress') AND deadline IS NOT NULL");

        // (2) Không hoạt động không có hard deadline — ScanInactiveB2CAsync:
        //     status IN (Ready, InProgress) && deadline == null && created_at < cutoff. Không lọc
        //     campaign_id: B2B không deadline đã reserve credit lúc Start, cũng phải được dọn.
        e.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_practice_sessions_b2c_active")
            .HasFilter("status IN ('Ready', 'InProgress') AND deadline IS NULL");

        // F14 — mẫu cộng đồng cho mốc đối chiếu (CriterionBenchmarkService): buổi B2C đã Scored, cùng
        // nghề + cùng ngôn ngữ, trong cửa sổ N ngày. Trước đây KHÔNG index nào che vị từ này ⇒ mỗi lượt
        // mở trang Kết quả là một lượt quét practice_sessions rồi nạp breakdown vào RAM.
        //
        // ⚠ PHÒNG XA, không phải chữa sự cố: bảng hiện mới vài trăm dòng nên chưa ai thấy chậm. Index
        // này để chi phí khỏi bám theo TOÀN BỘ lịch sử khi dữ liệu lớn dần.
        //
        // Hình: (job_category, language) lọc '=', created_at là RANGE (>= cutoff) ⇒ đặt cuối, đúng quy
        // tắc cột range đứng sau cột equality. KHÔNG khai DESC: truy vấn chỉ quét khoảng, không ORDER BY
        // (khác ix_practice_sessions_candidate_history — chỗ đó DESC là để khớp ORDER BY keyset).
        //
        // FILTER thay vì thêm 2 cột vào khoá: `campaign_id IS NULL AND status = 'Scored'` chọn lọc rất
        // mạnh (bỏ hết B2B + mọi buổi chưa chấm xong) và giữ index nhỏ. Partial index chỉ dùng được nếu
        // planner CHỨNG MINH được vị từ ⇒ hai vế đều so với HẰNG trong truy vấn (EF render literal cho
        // enum status và cho IS NULL — cùng lập luận đã verify ở ix_practice_sessions_deadline ngay trên).
        //
        // Phía session_criterion_scores KHÔNG cần index mới: nối vào bằng session_id, mà
        // ix_session_criterion_scores_session_id_criterion_id đã có session_id làm tiền tố trái.
        e.HasIndex(x => new { x.JobCategory, x.Language, x.CreatedAt })
            .HasDatabaseName("ix_practice_sessions_peer_benchmark")
            .HasFilter("campaign_id IS NULL AND status = 'Scored'");

        e.HasMany(x => x.Questions)
            .WithOne(q => q.Session)
            .HasForeignKey(q => q.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        e.HasMany(x => x.Answers)
            .WithOne(a => a.Session)
            .HasForeignKey(a => a.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK tới FileRecord (CV + JD) — giờ OPTIONAL (IsRequired(false)).
        // Restrict: không cho xóa file khi còn session tham chiếu.
        e.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(x => x.CvId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(x => x.JdId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
public class PracticeQuestionConfiguration : IEntityTypeConfiguration<PracticeQuestion>
{
    public void Configure(EntityTypeBuilder<PracticeQuestion> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Content).IsRequired();
        // Snapshot đáp án mẫu HR soạn (B2B). Cột `text` như Content — không HasMaxLength: trần độ dài
        // đã chặn ở CampaignService (nơi HR nhập, trả 400 có chữ), thêm trần ở đây chỉ đổi lấy một lỗi
        // Postgres thô ở giữa luồng tạo session, SAU khi đã giữ credit của tổ chức.
        e.Property(x => x.SampleAnswer);
        e.Property(x => x.OrderNo).IsRequired();
        e.Property(x => x.TimeLimitSec).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // INT-17b — độ sâu trong chuỗi đào sâu (0 = seed). Required + default 0 ⇒ row cũ nhận giá trị
        // hợp lệ ngay lúc AddColumn; migration còn backfill lại theo cây cho row thích ứng đã tồn tại.
        e.Property(x => x.Depth).IsRequired().HasDefaultValue(0);

        // Phỏng vấn THÍCH ỨNG — Kind lưu string (GEN-2). Rows cũ backfill 'Seed' (migration defaultValue).
        e.Property(x => x.Kind)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // RAG grounding — citation ĐÃ RESOLVE (jsonb NULLABLE — 3 trạng thái null/[]/non-empty, xem entity).
        // Converter null-safe (null → SQL NULL) mẫu Roadmap.SourceSessionIds. NULLABLE ⇒ migration AddColumn
        // KHÔNG cần defaultValue (row cũ nhận NULL) → né hẳn bug jsonb-rỗng-default F15.
        var refsConverter = new ValueConverter<List<Citation>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, KnowledgeJson.Options),
            v => v == null ? null : JsonSerializer.Deserialize<List<Citation>>(v, KnowledgeJson.Options));
        var refsComparer = new ValueComparer<List<Citation>?>(
            (a, b) => (a ?? new List<Citation>()).SequenceEqual(b ?? new List<Citation>()),
            v => v == null ? 0 : v.Aggregate(0, (h, r) => HashCode.Combine(h, r.GetHashCode())),
            v => v == null ? null : v.ToList());
        var refs = e.Property(x => x.GroundingRefs);
        refs.HasConversion(refsConverter);
        refs.Metadata.SetValueComparer(refsComparer);
        refs.HasColumnType("jsonb");

        // Tiêu chí NỘI DUNG mà câu hỏi nhắm tới (jsonb NULLABLE — null = không nhãn ⇒ chấm đủ rubric).
        // Cùng khuôn converter null-safe với GroundingRefs ngay trên: NULLABLE nên `AddColumn` KHÔNG
        // cần defaultValue → né hẳn bug F15 (EF scaffold `defaultValue: ""` cho cột jsonb làm Postgres
        // từ chối ngay tại ALTER TABLE, trong khi SQLite/EnsureCreated bỏ qua migration nên test xanh 100%).
        var targetConverter = new ValueConverter<List<Guid>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, KnowledgeJson.Options),
            v => v == null ? null : JsonSerializer.Deserialize<List<Guid>>(v, KnowledgeJson.Options));
        var targetComparer = new ValueComparer<List<Guid>?>(
            (a, b) => (a ?? new List<Guid>()).SequenceEqual(b ?? new List<Guid>()),
            v => v == null ? 0 : v.Aggregate(0, (h, g) => HashCode.Combine(h, g.GetHashCode())),
            v => v == null ? null : v.ToList());
        var targets = e.Property(x => x.TargetCriterionIds);
        targets.HasConversion(targetConverter);
        targets.Metadata.SetValueComparer(targetComparer);
        targets.HasColumnType("jsonb");

        e.HasIndex(x => new { x.SessionId, x.OrderNo }).IsUnique();

        // Phỏng vấn THÍCH ỨNG — 1 answer sinh TỐI ĐA 1 câu kế: unique filtered index trên
        // generated_from_answer_id (chỉ row thích ứng có giá trị; seed = null không tính). Là backstop
        // đồng thời cho re-upload / double-POST cùng frontier answer (insert thứ 2 vỡ unique). Filter
        // snake_case vì SQLite test dùng UseSnakeCaseNamingConvention (precedent DB5/DB19).
        e.HasIndex(x => x.GeneratedFromAnswerId)
            .IsUnique()
            .HasFilter("generated_from_answer_id IS NOT NULL");

        // INT-17b — gom lịch sử theo ĐÚNG chuỗi (root) + kiểm trần độ sâu. KHÔNG unique: một câu gốc có
        // nhiều tầng, và (root, depth) chỉ duy nhất trong chuỗi chứ không phải toàn buổi.
        e.HasIndex(x => new { x.SessionId, x.RootQuestionId, x.Depth });
    }
}

public class PracticeAnswerConfiguration : IEntityTypeConfiguration<PracticeAnswer>
{
    public void Configure(EntityTypeBuilder<PracticeAnswer> e)
    {
        e.ToTable(t => t.HasCheckConstraint(
            "ck_practice_answers_status", "status IN ('Uploaded', 'Transcribing', 'Transcribed', 'Scoring', 'Scored', 'Skipped', 'Failed')"));
        e.HasKey(x => x.Id);

        e.Property(x => x.AudioObjectKey).HasMaxLength(512);

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        e.Property(x => x.CreatedAt).IsRequired();

        // DB15 — bỏ UNIQUE(session_id, question_id) THỪA: quan hệ 1-1 với câu hỏi (HasForeignKey
        // question_id ở dưới) đã sinh UNIQUE ix_practice_answers_question_id; question_id là duy nhất
        // toàn cục ⇒ "tối đa 1 answer/câu" đã được đảm bảo. GIỮ cột dẫn session_id bằng index NON-unique
        // để các truy vấn EXISTS theo session_id (SessionAbandonSweeper quét mỗi 2', PracticeService
        // kiểm ≥1 answer) không seq-scan (không có index session_id đứng riêng nào khác).
        e.HasIndex(x => x.SessionId);

        // DB5 — hỗ trợ StuckAnswerRepublisher (quét mỗi 2', trước đây seq-scan cả bảng). Lọc theo
        // Status ∈ {Uploaded,Scoring} rồi so LastScoringPublishedAt (null/aged). Composite non-partial
        // (status, last_scoring_published_at) → leading col status thu hẹp tập, col 2 phục vụ so mốc thời gian.
        e.HasIndex(x => new { x.Status, x.LastScoringPublishedAt })
            .HasDatabaseName("ix_practice_answers_status_lsp");

        // Restrict (KHÔNG Cascade): tránh multiple cascade paths.
        // Answer vẫn bị xóa khi session xóa qua đường session -> answers ở trên.
        e.HasOne(x => x.Question)
            .WithOne(q => q.Answer)
            .HasForeignKey<PracticeAnswer>(x => x.QuestionId)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasMany(x => x.Scores)
            .WithOne(s => s.Answer)
            .HasForeignKey(s => s.AnswerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AnswerScoreConfiguration : IEntityTypeConfiguration<AnswerScore>
{
    public void Configure(EntityTypeBuilder<AnswerScore> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Score).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.AttemptNo).IsRequired();
        e.Property(x => x.RubricVersion).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // Một tiêu chí chấm cho một answer ở một lần chấm là duy nhất
        e.HasIndex(x => new { x.AnswerId, x.CriterionId, x.AttemptNo }).IsUnique();

        e.HasOne(x => x.Criterion)
            .WithMany()
            .HasForeignKey(x => x.CriterionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// BC9 — breakdown điểm mỗi tiêu chí của buổi luyện B2C (ghi khi session Scored).
public class SessionCriterionScoreConfiguration : IEntityTypeConfiguration<SessionCriterionScore>
{
    public void Configure(EntityTypeBuilder<SessionCriterionScore> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.CriterionName).HasMaxLength(128).IsRequired();
        e.Property(x => x.AverageScore).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.MaxScore).IsRequired();
        e.Property(x => x.Percentage).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.Weight).HasColumnType("numeric(5,4)").IsRequired();
        e.Property(x => x.NeedsImprovement).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // 1 row / (buổi, tiêu chí) — idempotent theo session (xoá + ghi lại khi tính lại).
        e.HasIndex(x => new { x.SessionId, x.CriterionId }).IsUnique();

        e.HasOne(x => x.Session)
            .WithMany(s => s.CriterionScores)
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // criterion_id → rubric_criteria (Restrict): giữ điểm lịch sử, chặn xoá tiêu chí đang tham chiếu.
        e.HasOne<RubricCriterion>()
            .WithMany()
            .HasForeignKey(x => x.CriterionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class SessionCriterionEvidenceConfiguration : IEntityTypeConfiguration<SessionCriterionEvidence>
{
    public void Configure(EntityTypeBuilder<SessionCriterionEvidence> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.CriterionName).HasMaxLength(128).IsRequired();
        e.Property(x => x.State).HasMaxLength(16).IsRequired();

        // Tập ĐÓNG của evidence state, chốt ở tầng DB cho MỌI đường ghi — đối xứng
        // `ck_practice_sessions_seniority` (cùng PR đã làm cho seniority, chỗ này thì quên).
        //
        // ⚠ Vì sao KHÔNG dựa vào guard C# ở AnswerService: `state` là `varchar(16)`, và thêm một state
        // dài hơn 16 ký tự sẽ vỡ trên Postgres trong khi **SQLite không enforce độ dài varchar** ⇒ test
        // xanh 100%. Đúng hình dạng sự cố S11 (`funded_by varchar(16)` vs enum `SubscriptionMetered`
        // 19 ký tự: 1569 test SQLite xanh, mọi reserve gói metered hỏng trên prod). CHECK làm giá trị
        // lạ hỏng NGAY ở migration/insert đầu tiên thay vì hỏng lặng lẽ theo độ dài chuỗi.
        e.ToTable(t => t.HasCheckConstraint(
            "ck_session_criterion_evidence_state",
            "state IN ('UNKNOWN', 'PARTIAL', 'SATISFIED', 'FAILED')"));
        var evidenceComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v == null ? 0 : v.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())),
            v => v == null ? new List<string>() : v.ToList());
        var found = e.Property(x => x.EvidenceFound).HasConversion(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()).HasColumnType("jsonb");
        found.Metadata.SetValueComparer(evidenceComparer);
        var missing = e.Property(x => x.MissingEvidence).HasConversion(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()).HasColumnType("jsonb");
        missing.Metadata.SetValueComparer(evidenceComparer);
        e.HasIndex(x => new { x.SessionId, x.CriterionId }).IsUnique();
        e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<RubricCriterion>().WithMany().HasForeignKey(x => x.CriterionId).OnDelete(DeleteBehavior.Restrict);
    }
}
