using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.CampaignService.Migrations
{
    /// <summary>
    /// E11c — bảng <c>ranking_overrides</c>: LỊCH SỬ điều chỉnh kết quả của HR (append-only, một dòng mỗi lần
    /// đặt/huỷ override). Trước đó 5 cột <c>override_*</c> trên <c>campaign_rankings</c> chỉ giữ lần MỚI NHẤT
    /// (huỷ = null hết) và trail duy nhất là chuỗi tự do trong <c>audit_logs.summary</c> — HR không đọc được.
    ///
    /// <para><b>Up() = CreateTable (additive) + MỘT câu <c>Sql()</c> BACKFILL</b> dựng lại lịch sử cũ từ
    /// <c>audit_logs</c> (<c>action = 'OverrideResult'</c>; dev 4 dòng · prod 22 dòng). Parse bằng
    /// <c>substring(summary from 'pattern')</c> TỪNG TRƯỜNG RIÊNG, mỗi pattern đúng một quantifier — CỐ Ý không
    /// viết một regex lớn trộn <c>.*?</c> với <c>.*</c>: Postgres ARE lấy greediness của quantifier ĐẦU cho cả
    /// biểu thức nên bản trộn lệch trong im lặng. Định dạng nguồn (do <c>CampaignService.OverrideResultAsync</c>
    /// ghi, không đổi từ E11b):
    /// <c>Override session {sid}: score={x|—}, result={Pass|Fail|—}. Lý do: {note}</c> ·
    /// <c>Huỷ override session {sid} (về điểm AI). Lý do: {note}</c>.</para>
    ///
    /// <para>Luật parse: score chỉ cast khi khớp <c>^[0-9]+(\.[0-9]+)?$</c> (chuỗi gốc là <c>decimal.ToString()</c>
    /// theo culture máy chủ ⇒ có thể là <c>72,5</c>; đổi <c>,</c>→<c>.</c> trước), không khớp ⇒ NULL, KHÔNG ném.
    /// Không tìm thấy <c>'. Lý do: '</c> ⇒ note = nguyên summary (không mất chữ). Session không còn ranking ⇒ bỏ
    /// dòng (preflight đếm trước, kỳ vọng 0). <c>actor_email = NULL</c> (audit không có email — BK23: null = không
    /// biết), <c>source = 'AuditBackfill'</c>, <c>created_at = audit_logs.at</c>.</para>
    ///
    /// <para>Thứ tự deploy: bảng mới nằm im tới khi code dùng ⇒ apply TRƯỚC deploy là an toàn (chiều ngược lại —
    /// code trước migration — là <c>42P01 relation does not exist</c> ở mọi lần override; repo đã dính "code đi trước
    /// migration" ba lần). <see cref="Down"/> = DropTable — mất cả dòng Live ghi sau apply; backfill chạy lại được
    /// từ audit_logs nhưng dòng Live thì không.</para>
    ///
    /// <para>Backfill là Postgres-only (SQLite/EnsureCreated bỏ qua migration) ⇒ KHÔNG test .NET nào phủ — đọc
    /// bằng mắt + verify trên DB thật bằng preflight/đếm sau apply. KHÔNG tự chạy <c>dotnet ef database update</c>
    /// lên DB chung.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddRankingOverridesE11c : Migration
    {
        // Mọi câu kết thúc bằng ';' — thiếu là vỡ idempotent script (bài học AddAuditColumnsAndTypes).
        private const string BackfillFromAuditLogs = """
            INSERT INTO ranking_overrides
                (id, ranking_id, campaign_id, session_id, kind, score, result, note, actor_user_id, actor_email, source, created_at)
            SELECT gen_random_uuid(),
                   r.id,
                   a.entity_id,
                   r.session_id,
                   p.kind,
                   CASE WHEN p.score_text ~ '^[0-9]+(\.[0-9]+)?$' THEN p.score_text::numeric(5,2) ELSE NULL END,
                   p.result,
                   p.note,
                   a.actor_user_id,
                   NULL,
                   'AuditBackfill',
                   a.at
            FROM audit_logs a
            CROSS JOIN LATERAL (
                SELECT
                    substring(a.summary from 'session ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') AS session_text,
                    CASE WHEN a.summary LIKE 'Huỷ override session %' THEN 'Clear' ELSE 'Set' END AS kind,
                    replace(NULLIF(substring(a.summary from 'score=([^,]+), result='), '—'), ',', '.') AS score_text,
                    substring(a.summary from 'result=(Pass|Fail)\.') AS result,
                    CASE WHEN strpos(a.summary, '. Lý do: ') > 0
                         THEN substr(a.summary, strpos(a.summary, '. Lý do: ') + length('. Lý do: '))
                         ELSE a.summary END AS note
            ) p
            JOIN campaign_rankings r
              ON p.session_text IS NOT NULL
             AND r.session_id = p.session_text::uuid
             AND r.campaign_id = a.entity_id
            WHERE a.action = 'OverrideResult';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ranking_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ranking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    score = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    result = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    note = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ranking_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_ranking_overrides_campaign_rankings_ranking_id",
                        column: x => x.ranking_id,
                        principalTable: "campaign_rankings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ranking_overrides_ranking_id_created_at",
                table: "ranking_overrides",
                columns: new[] { "ranking_id", "created_at" });

            // Backfill lịch sử cũ từ audit_logs (xem <summary>). Chỉ Npgsql — SQLite (test) không chạy migration.
            migrationBuilder.Sql(BackfillFromAuditLogs);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ranking_overrides");
        }
    }
}
