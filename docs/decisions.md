# ISAS — Decision Log (ADR-lite)

> **Vì sao** của các quyết định kiến trúc — phần dễ mất nhất (lý do thường chỉ nằm trong đầu/chat). Phiên/người mới đọc đây để **không lật lại quyết định đã cân nhắc**.
> Định dạng mỗi mục: *Quyết định · Vì sao · Phương án bị loại*. "Cái gì" (thiết kế chi tiết) xem `architecture.md` + `services/`.

---

## D1 — Engine + Orchestrator, 6 service (2026-06-24)
**Quyết định:** Tái dùng InterviewService làm **engine phỏng vấn dùng chung** (B2B & B2C, phân biệt bằng `campaign_id`); CampaignService chỉ điều phối B2B.
**Vì sao:** máy chấm điểm (Whisper+Gemini+RabbitMQ+state machine+republisher) đã chạy ổn — chép lại là rủi ro thời gian lớn nhất dự án.
**Bị loại:** (a) nhét toàn bộ B2B vào CampaignService → phải nhân đôi engine chấm; (b) mỗi module 1 microservice → over-split cho team 4 người.

## D2 — Giữ DB-per-service, không gộp mono (2026-06-24)
**Vì sao:** cái đắt của microservice là **transaction xuyên service**, không phải số DB (trên cùng 1 Postgres chỉ là thêm connection string). Pattern đã có sẵn.
**Bị loại:** mono-DB để "giảm phức tạp" — giảm nhầm chỗ.

## D3 — Chưa chuyển gRPC (2026-06-24)
**Quyết định:** giữ REST cho edge + nội bộ; chỉ cân nhắc gRPC cho 3 hop east-west **nếu** rubric thưởng / team muốn học.
**Vì sao:** edge (browser/YARP) buộc REST; bottleneck là Whisper/Gemini không phải JSON; hybrid REST+gRPC thêm phức tạp + tooling khó demo.

## D4 — Billing = credit (lượt phỏng vấn), KHÔNG metering token (2026-06-24)
**Vì sao:** khách hiểu "lượt", không hiểu "token"; bài có time-limit → chi phí AI/lượt chặn trên → giá cố định hợp lý. Token chỉ là **giá vốn nội bộ**.
**Bị loại:** tính tiền theo token LLM thô — khó báo giá, biến động, overkill.

## D5 — Credit theo ORG, không theo cá nhân (2026-06-24)
**Vì sao:** B2B bán cho doanh nghiệp; **postpaid ép phải org-level** (hợp đồng/MST); HR sub-account tiêu credit của org.
**Bị loại:** credit gắn cá nhân Employer (vỡ khi có nhiều HR / trả sau).
**Phạm vi:** áp cho **B2B** — "cá nhân" ở đây = HR/Employer, **không** phải end-user B2C. Ví **B2C = cá nhân, prepaid** (xem **D15**), cùng cơ chế nhưng khác chủ ví; postpaid vẫn org-only nên lý do trên còn nguyên.

## D6 — Prepaid + Postpaid (trả sau) (2026-06-25)
**Quyết định:** Prepaid (pack OneTime) + Postpaid (org được PlatformAdmin duyệt → dồn nợ tới hạn mức → hóa đơn cuối kỳ → tất toán PayOS).
**Vì sao:** doanh nghiệp lớn cần mua chịu/hóa đơn. Postpaid cần **hạn mức + duyệt + đình chỉ** vì rủi ro nợ xấu (AI cost đốt thật).
**Bị loại:** chỉ prepaid (khó bán cho công ty lớn); Subscription để **phase 2**.

## D7 — Tiêu credit: Reserve → Consume(Scored) → Release (2026-06-25)
**Quyết định:** giữ chỗ khi bắt đầu, trừ thật khi `Scored`, nhả khi bỏ ngang/lỗi. Idempotent theo `session_id`.
**Vì sao:** ứng viên rớt mạng/lỗi mic/bỏ ngang **không làm org mất credit oan** (giảm ticket refund); reservation **bảo vệ in-flight** khi org downgrade/hết hạn.
**Bị loại:** trừ-khi-bắt-đầu / trừ-khi-nộp-câu-đầu (vẫn tính tiền bài dở).

## D8 — Danh tính ứng viên B2B = magic-link → account Candidate nhẹ (2026-06-24)
**Vì sao:** engine cần `candidate_id` + ownership; tạo account tối thiểu → **tái dùng nguyên cơ chế** JWT/ownership.
**Bị loại:** token-as-identity (phải chế special-case trong engine).
*(Cập nhật 2026-07-02: **2 đường phát link** — **danh sách email** HR upload (mời thẳng) HOẶC **từ shortlist sàng CV** (tách email từ CV — D18). Đường CV: provision xong **tự gắn** account vào `campaign_candidates.candidate_id` (qua `campaign_invitations.campaign_candidate_id`) → **CV↔account↔session nối liền**, HR xem điểm sàng + điểm phỏng vấn cạnh nhau; CV không tách được email → HR `PATCH` bổ sung rồi mời. Chi tiết: [services/campaign.md](services/campaign.md) §Distribution.)*

## D9 — Tiêu chí campaign: text → CÓ CẤU TRÚC (AI đề xuất + HR duyệt) (2026-06-24)
**Vì sao:** pipeline chấm cần tiêu chí có `weight`/`max_score` và `answer_scores.criterion_id` là FK → **không chấm được trên text thô**. Materialize thành `rubric_criteria(campaign_id)` → chấm như rubric thường + ranking có trọng số.
**Bị loại:** chấm thẳng trên `criteria_text` (lệch cấu trúc dữ liệu).

## D10 — Ranking event-driven (`SessionScored` → read-model) (2026-06-25)
**Vì sao:** tránh gọi HTTP xuyên service mỗi lần xem dashboard + tải dồn khi nhiều ứng viên xong cùng lúc; CampaignService giữ `campaign_rankings` đọc local.
**Bị loại:** HTTP pull tính ranking on-demand (timeout khi đông, recompute mỗi lần xem). Giữ 1 endpoint backfill làm fallback.

## D11 — Soft delete + Audit log (2026-06-25)
**Vì sao:** B2B có thể bị **kiện/đối chất** kết quả → cần lịch sử. `deleted_at` + purge file 90 ngày (giữ điểm/transcript). `audit_logs` ghi ai sửa gì.
**Bị loại:** hard delete (mất trắng dữ liệu pháp lý).

## D12 — `order_code` = time + random (2026-06-25; trần PayOS verify 2026-06-28)
**Vì sao:** không đoán được (chống cào) + **trong trần số của PayOS**. Đụng UNIQUE → regenerate.
**Bị loại:** auto-increment (lộ số đơn); **snowflake 64-bit** — vượt trần PayOS nên loại.
**✅ Trần PayOS (verify payos.vn):** `orderCode` là **số nguyên dương ≤ 9.007.199.254.740.991** (2^53−1 — PayOS xử lý như JS number), **duy nhất vĩnh viễn**. Snowflake 64-bit (tới ~9,2×10¹⁸) **vượt trần** → đúng lý do loại. Scheme `YYMMDDHHmmss`+random (giữ < 9,007×10¹⁵) **nằm trong trần**. Verify thêm: webhook ký **HMAC-SHA256** (sort key A→Z, nối `key=value&`, checksum key); `description` **ngắn** (≤25 ký tự liên kết payOS, 9 ký tự VietQR không liên kết) → đơn ISAS đặt mô tả gọn. Bảng đầy đủ: [services/payment.md](services/payment.md) §PayOS.

## D13 — Anti-cheat = FLAG cho HR, không auto-hủy (2026-06-25)
**Vì sao:** false-positive auto-hủy giết oan ứng viên thật. Tín hiệu (tab/focus/paste/multi-voice) → cảnh báo, **HR quyết**.
**Bị loại:** auto-hủy bài theo tín hiệu.

## D14 — Doc: doc là source of truth, code disposable (2026-06-24)
**Vì sao:** code trên branch còn dở, sẽ sửa theo doc; mirror code vào doc tạo doc sai/tạm.
**Hệ quả:** sửa doc về **thiết kế đúng**, không bám code dở; doc tách **theo service** trong `docs/services/`, global ở `docs/`.

## D15 — Ví B2C = personal credit account, prepaid-only (2026-06-26)
**Quyết định:** B2C trả tiền bằng **cùng** credit/PayOS/reserve→consume như B2B, chỉ khác **chủ ví là User** (cá nhân) thay vì Org. Tổng quát hóa "chủ ví": account có `owner_type` (`Org`|`User`) + `owner_id`. Personal account **chỉ Prepaid** (pack OneTime); **postpaid + hạn mức + hóa đơn vẫn CHỈ Org**.
**Vì sao:** [architecture.md](architecture.md) §4.1 đã chốt "B2C tự trả credit". Tái dùng nguyên sổ cái + reserve/consume (**D7**) → không nhân đôi máy thanh toán. Postpaid giữ org-only nên lý do **D5** (postpaid ép org/hợp đồng) không vỡ. B2C không có orchestrator → **InterviewService** gọi reserve khi tạo session (Campaign lo phần B2B). *(Cập nhật BK14 2026-07-16: B2B reserve **cũng** chuyển sang InterviewService — Campaign chỉ gửi `campaign.OrgId` lúc Start, reserve-first tránh orphan session_id; xem D23 §Hệ quả.)*
**Bị loại:** (a) bảng `personal_credit_accounts` riêng → nhân đôi sổ cái + 2 code path; (b) "org ảo" cho mỗi candidate → bẩn bảng org, auth đẻ org rác.
**Hệ quả:** `org_credit_accounts` → `credit_accounts(owner_type, owner_id)`; internal API reserve nhận `{ ownerType, ownerId, sessionId }`. Chi tiết [services/payment.md](services/payment.md).

## D16 — B2C là DÒNG SẢN PHẨM ngang hàng B2B, không chỉ là engine (2026-06-27)
**Quyết định:** ISAS giao **2 dòng sản phẩm**: B2C (luyện phỏng vấn cá nhân) **và** B2B (tuyển dụng), cùng dùng engine InterviewService + AIService + PaymentService. B2C có **scope/module/stream/task/E2E riêng** trong doc (BC1–BC3, stream **S5**), không bị gộp ẩn vào B2B.
**Vì sao:** trước đây doc khung "sản phẩm = B2B, B2C = engine dùng lại" → B2C biến mất khỏi [work-division.md](work-division.md) (5 module + 4 stream toàn B2B) và [tasks.md](tasks.md) (0 task B2C), kể cả path thanh toán ví cá nhân đã thiết kế ở **D15** cũng không ai sở hữu. B2C engine + lịch sử **đã chạy** nên chi phí "nâng lên sản phẩm" thấp — chủ yếu nối thanh toán ví cá nhân.
**Bị loại:** (a) coi B2C là engine/demo (không giao) — sai phạm vi; (b) gộp B2C vào S1/S3/S4 không tách stream — B2C dễ bị coi nhẹ, không có owner.
**Hệ quả:** thêm **S5 — B2C Product** + module BC1–BC3 + task BC1–BC5; khung "2 dòng sản phẩm" đồng bộ ở AGENTS/README/architecture; **Định nghĩa Xong** thêm luồng E2E B2C ([AGENTS.md](../AGENTS.md)). *(Cập nhật sau: D17 thêm module BC4 + task BC6–BC8; sau đó BC9–BC11 — tổng kết điểm/nhận xét + seed rubric B2C. Danh sách task hiện hành xem [tasks.md](tasks.md).)*

## D17 — B2C thêm "Phân tích CV" (BC4), dùng AIService đồng bộ (2026-06-27)
**Quyết định:** B2C có thêm **phân tích CV** (module BC4): (a) **feedback CV độc lập** (tóm tắt + mạnh/yếu + gợi ý); (b) **điểm khớp CV↔JD** (% + kỹ năng thiếu/đủ); (c) mục **"CV vs câu trả lời"** trong báo cáo buổi luyện. Sinh câu hỏi từ CV/JD (đã có ở BC2) **giữ nguyên**. Triển khai bằng **AIService endpoint mới `/analyze-cv` — HTTP đồng bộ** (1 call Gemini, **KHÔNG** qua RabbitMQ/worker vì không có audio); AIService **vẫn stateless**, InterviewService lưu bảng `cv_analyses`. **Miễn phí (không trừ credit) trong phase 1.**
**Vì sao:** phân tích CV là giá trị B2C rõ rệt (định hướng luyện tập). Tái dùng pattern **đồng bộ như `generate-questions`** thay vì pipeline chấm bất đồng bộ — pipeline async chỉ cần cho **audio nặng** (Whisper). Giữ ranh giới **AI-không-ghi-DB** ([architecture.md](architecture.md) §5).
**Bị loại:** (a) nhét vào pipeline chấm RabbitMQ — overkill, không có audio; (b) AIService tự lưu kết quả — vỡ quy ước AI-không-DB; (c) tính phí mỗi lần phân tích — phase 1 để miễn phí cho đơn giản, **team xác nhận lại** nếu muốn trừ credit (khi đó thêm reserve riêng).
**Hệ quả:** AIService `/analyze-cv` + prompt `build_cv_analysis_prompt` ([services/ai.md](services/ai.md)); InterviewService `/practice/cv-analysis` + bảng `cv_analyses` ([services/interview.md](services/interview.md)); task `BC6`–`BC8` ([tasks.md](tasks.md)); module **BC4** + S5 deliverable ([work-division.md](work-division.md)). ⚠ *Mở rộng phạm vi B2C — cân nhắc với deadline capstone (BC8 đối chiếu CV↔transcript là task nặng nhất, để cuối).*

## D18 — Lọc CV hàng loạt B2B: 1 engine `analyze-cv`, tái dùng `campaign_criteria`, KHÔNG dùng `file_records` (2026-06-30)
**Quyết định:** B2B "sàng lọc CV hàng loạt" dùng **chung engine** AIService `/analyze-cv` với B2C (**D17**): B2C gọi **sync HTTP** (1 CV); B2B gọi **async** qua queue mới `cv_screening_queue` + callback (N CV). Lọc **hybrid**: rule cứng (`required_skills`/`keywords_any`/`min_years_experience` trên `campaigns`) chạy ở CampaignService **TRƯỚC**, rồi AI chấm khớp theo **`campaign_criteria`** (tái dùng nguyên rubric + weight Σ=1, như **D9**). Kết quả lưu **2 bảng mới trong `isas_campaign`** (`campaign_candidates` + `candidate_criterion_scores`); ranking = **derived** (`overall_match_score`). AIService **vẫn không ghi DB**.
**Vì sao:** (a) 1 engine 2 transport → không đẻ engine phân tích CV thứ 2; tái dùng publisher/worker/callback (**D1**) + `campaign_criteria` (**D9**). (b) **Không dùng `file_records`** (Interview): `user_id` non-nullable gắn ứng viên — HR đổ CV hộ không khớp model candidate-centric; CampaignService đã có `ParserService` (PdfPig) riêng → parse tại chỗ, không phụ thuộc Interview. (c) Hard-filter **trước** AI → chỉ tốn AI cho CV qua vòng cứng. (d) Engine `/analyze-cv` chỉ ăn **text** (không như pipeline chấm cần audio/Whisper) nên S3 chỉ là **archival tùy chọn**, ngoài critical path.
**Bị loại:** (a) nhét CV thành "session rỗng" trong engine phỏng vấn — `answer_scores` gắn `practice_answers`/audio, vỡ state machine; (b) tái dùng `file_records` cho CV HR-upload — vướng `user_id`; (c) bảng `campaign_rankings` (read-model điểm **hậu phỏng vấn**, **D10**) cho ranking CV — khác mục đích, ranking CV là **query** không cần bảng; (d) bảng `cv_analyses` riêng cho B2B — gộp luôn vào `campaign_candidates`.
**Hệ quả:** AIService `/analyze-cv` mở rộng res cho B2B (`criterionMatches`/`overallMatchScore`) + worker queue `cv_screening_queue` ([services/ai.md](services/ai.md)); CampaignService `/campaign/{id}/candidates` + callback `/internal/campaign-candidates/…` + 2 bảng + 3 cột rule ([services/campaign.md](services/campaign.md)); task `C13`–`C15`, cần `BC6` (engine) ([tasks.md](tasks.md)). B2C `BC6`/`BC7` (D17) **không đổi** — chỉ tái dùng.

## D19 — Sàng CV: 1 trong 2 cách lọc, MIỄN PHÍ phase 1 + cap (không tiêu interview-credit) (2026-06-30)
**Quyết định:** B2B là **app lọc ứng viên** với **2 phương thức song song** — **(1) qua CV** (D18, C13–C15), **(2) qua phỏng vấn AI** (engine hiện có). Sàng CV là funnel **TÙY CHỌN** (mời thẳng không sàng vẫn được), có thể nối sang phỏng vấn (`Invited` → magic-link). **Sàng CV KHÔNG tiêu interview-credit** và **MIỄN PHÍ phase 1** (như analyze-cv B2C, **D17**). Billing **chỉ** phát sinh ở **lượt phỏng vấn thật** (`Invited` → reserve→consume, **D7** nguyên vẹn). Chặn chi phí Gemini bằng **hard-filter-trước-AI + cap số CV/campaign**. Lifecycle ứng viên (`Pending→Filtered→Analyzing→Analyzed→Invited`, + `Rejected`/`AnalysisFailed`) có state machine riêng — chi tiết [services/campaign.md](services/campaign.md).
**Vì sao:** `1 credit = 1 lượt phỏng vấn có audio` ([services/payment.md](services/payment.md)) — trừ credit cho việc **đọc CV** là **sai semantics** và **double-charge** khi ứng viên qua sàng còn đi phỏng vấn. Tách "chi phí đọc CV" (giá vốn nội bộ, chặn bằng cap) khỏi "giá 1 lượt phỏng vấn". Tái dùng nguyên D7/D17 → không nhân đôi máy thanh toán.
**Bị loại:** (a) trừ interview-credit cho sàng — sai semantics + double-charge; (b) **credit sàng riêng** ngay phase 1 — thêm pack/reserve/consume/ledger, phức tạp thừa; (c) sàng **bắt buộc** trước mọi mời — mất tính tùy chọn (có HR mời ứng viên đã biết). **Phase 2:** nếu chi phí AI lớn → cân nhắc **loại credit sàng riêng** (team xác nhận lại — y hệt D17 để ngỏ chuyện tính phí).
**Hệ quả:** ghi chú "sàng không bill" ở [services/payment.md](services/payment.md) §Reserve→Consume; cost+cap ở [services/ai.md](services/ai.md) §`cv_screening_queue`; state machine + luồng tiền ở [services/campaign.md](services/campaign.md) §Lọc ứng viên qua CV; "2 phương thức lọc" ở [architecture.md](architecture.md) §4.2.

## D20 — B2C Roadmap ôn tập cá nhân hoá (2026-07-02)
**Quyết định:** B2C thêm **roadmap ôn tập lý thuyết cá nhân hoá** (module **BC5**): chọn `jobCategory` (BA/FE/BE) + `level` (Fresher/Junior/Middle/Senior) → hệ thống dựa **report các buổi đã chấm** (`session_criterion_scores` — điểm yếu, BC9) + **CV** (tự lấy nếu có) → AIService sinh **milestone roadmap**; mỗi mile gồm **lesson** = *lý thuyết trước* (AI sinh bám điểm yếu, lưu lại) → *luyện session* (engine chấm như thường); xong mỗi mile đo **độ cải thiện**, xong roadmap → **report cuối** (radar + đánh giá tiêu chí theo ngưỡng level + kết luận mạnh/yếu/cần cải thiện). Dữ liệu ở **`isas_interview`** (3 bảng `roadmaps`/`roadmap_milestones`/`roadmap_lessons`); AI **sync + stateless** (`/generate-roadmap` · `/generate-lesson-theory` · `/summarize-roadmap`). **Billing:** lý thuyết **miễn phí** (text-only, như D17); **session luyện = practice session B2C bình thường** → reserve→consume 1 credit (D7/D15 **nguyên vẹn**, không cơ chế tiền mới).
**Vì sao:** BC9/BC10 chỉ ra *yếu ở đâu* rồi dừng — không có *lộ trình luyện tiếp* ⇒ retention B2C cụt. Roadmap đóng vòng lặp **chấm xong → biết yếu gì → lộ trình mile/lesson → đo cải thiện** — đúng lý do người luyện quay lại (và mua credit tiếp). Tái dùng nguyên engine + BC9/BC10/BC11, thêm 3 bảng + 3 endpoint sync — không nhân đôi máy chấm, không infra mới.
**Bị loại:** (a) LMS/curriculum tĩnh soạn tay — không cá nhân hoá theo điểm yếu, tốn công content; (b) RoadmapService riêng — over-split cho team 4 người (như D1), data gắn chặt session/điểm ở Interview; (c) tính phí sinh lý thuyết/roadmap ngay — text-only rẻ, để **miễn phí phase 1** như D17/D19 (*team xác nhận lại nếu muốn thu*).
**Hệ quả:** spec API + DB + state machine ở [services/interview.md](services/interview.md) §Roadmap ôn tập; 3 endpoint AI ở [services/ai.md](services/ai.md); task `BC12`–`BC15` ([tasks.md](tasks.md)); module **BC5** + S5 ở [work-division.md](work-division.md); luồng B2C bước 6 ở [architecture.md](architecture.md) §4.1. Ngưỡng level (Fresher 50 · Junior 60 · Middle 70 · Senior 80) là config — **chốt khi build**.

## D21 — Bỏ giới hạn TỔNG buổi, chỉ giới hạn TỪNG CÂU (tạm thời) (2026-07-11)
**Quyết định:** Áp **cả B2B & B2C** — **bỏ** giới hạn thời gian tổng buổi (`campaigns.time_limit_minutes` giữ cột nhưng **không enforce**); **chỉ** giới hạn **từng câu** (`campaign_questions.time_limit_seconds` → `practice_questions.time_limit_sec`). Hết giờ 1 câu → **chốt riêng câu đó** (có ghi âm → nộp; chưa → `Skipped`) → **sang câu kế**, KHÔNG đóng cả buổi. Auto-submit/`SessionAbandoned` (chống reservation treo B2B) bám **`campaigns.expires_at`** thay cho time-limit tổng.
**Vì sao:** giới hạn từng câu đủ chặn trần chi phí AI (mỗi câu có trần thời gian) + trải nghiệm rõ theo câu; tổng buổi thêm timer/state phức tạp, dễ auto-submit oan khi mạng chập chờn. Đánh dấu **🔸 tạm thời** — bật lại tổng buổi = enforce `time_limit_minutes` + khôi phục trigger ở [services/interview.md](services/interview.md) §State machine.
**Bị loại:** (a) giữ cả tổng + từng câu — thừa, xung đột khi 1 trong 2 hết trước; (b) chỉ tổng, bỏ từng câu — mất kiểm soát 1 câu lê thê đốt Whisper/Gemini.
**Hệ quả:** sửa `time_limit_minutes` ([services/campaign.md](services/campaign.md)) + trigger auto-submit ([services/interview.md](services/interview.md)); task **I2** ([tasks.md](tasks.md)); bỏ `timeLimitMinutes` khỏi ví dụ body (vẫn nhận optional cho tương thích).

## D22 — Phân tích CV TÍNH PHÍ (chốt BK5, đảo D17 "free phase 1") (2026-07-12)
**Quyết định:** Phân tích CV (feedback + khớp JD, `/analyze-cv` B2C — BC7) **TÍNH PHÍ**: trừ 1 credit ví cá nhân (B2C) / org (B2B) — theo rules.md **BC-4**. **Đảo** phần "miễn phí phase 1" của **D17/D19** *chỉ cho phân tích CV*. **KHÔNG đổi:** lý thuyết roadmap (D20) vẫn **miễn phí** (text-only); sàng CV hàng loạt B2B (D18/C13–C15) vẫn **không tiêu interview-credit** (funnel, chặn bằng cap) — BC-4 nói "trừ credit" áp cho *lượt phân tích CV chủ động*, không phải mỗi CV trong batch sàng.
**Vì sao:** team chốt phân tích CV là dịch vụ có giá trị (1 Gemini call) → thu phí như 1 lượt; tránh lạm dụng free. Giữ nguyên D7/D15 (reserve→consume theo session/thao tác), không thêm cơ chế tiền mới.
**Hệ quả:** rules.md BC-4 ✓ (đã charge) · interview.md §cv-analysis (miễn phí→tính phí) · **task code còn lại:** wire reserve/consume (P4/P5) vào BC7 `cv-analysis` (endpoint hiện chưa trừ credit) — [tasks.md](tasks.md) BC7-billing. Ngưỡng/giá = `1 credit/lượt` (như D4).

## D23 — Giữ mô hình role 2-tầng: platform role + org_role; HrMember là THÀNH VIÊN thuộc org (2026-07-13)
**Quyết định:** Phân quyền **2 tầng, giữ nguyên** (đã verify live API sweep 2026-07-13). **(1) Platform role** (claim JWT `role` ∈ `Candidate`/`Employer`/`Admin`) — gác cửa bằng `[Authorize(Roles)]` ở controller (A5, **offline** GEN-3, không call Auth lúc chạy). **(2) Org role** (claim `org_id`+`org_role` ∈ `OrgAdmin`/`HrMember`, **chỉ khi user thuộc org** — bảng `org_members`) — **HrMember không phải platform role riêng mà là tư cách thành viên THUỘC 1 org**; enforce bằng business rule trong code: A4 (HrMember→403 billing money-mutation), A6 (chỉ OrgAdmin quản thành viên), data-scope theo `org_id`. **Billing + campaign gắn theo ORG, không theo cá nhân HR** (D5/AUTH-8). Chỉ **2** org-role (đủ capstone).
**Vì sao:** đúng pattern multi-tenant (GitHub org / Slack workspace): platform role = *"được vào cửa nào"* (gate tĩnh, ma trận A5 sạch "0 endpoint trần"); org_role = *"trong nhà làm được gì"* (rule động). Nâng HrMember thành **platform role** sẽ (a) không diễn đạt được "HR *của org nào*" (role platform là global), (b) phá ma trận A5, (c) bắt re-issue token mỗi lần đổi vai trò trong org. 2 org-role đủ cho demo B2B.
**Bị loại:** (a) HrMember = platform role thứ 4 — phá tách 2 tầng, không mang ngữ cảnh org; (b) tách nhỏ org-role (Interviewer/Viewer/Billing-only…) — over-engineer cho capstone, để **phase-2**; (c) RBAC policy-based đầy đủ (claims + resource policy) — thừa cho 4-người/demo.
**Hệ quả (gap tầng org = follow-up, KHÔNG phải lỗi thiết kế):** **A6b-phase2** invite qua email + **attach account có sẵn** (nay HR có account trước → thêm vào org bị **409**) · **BK14 (partial, commit 3399765):** ✅ **reserve B2B org credit = InterviewService** (reserve-first tránh orphan chicken-and-egg session_id; Campaign gửi `campaign.OrgId` lúc Start, ví org hết → 402 no-session) — **còn mở:** JWT **không refresh khi đổi role** (PATCH OrgAdmin↔HrMember xong token cũ giữ role cũ tới re-login) · DECISION retake sau Completed · orgName resolve · ratify: HrMember vẫn **xem** billing (GET), chỉ mutation bị chặn. Spec: [services/auth.md](services/auth.md) §Organization; rules **AUTH-4/5/6/8**.

## Ghi chú giải quyết backlog (2026-07-12)
- **BK9 → doc-only (đã làm):** event-bus convention vào [architecture.md](architecture.md) §6 (exchange `interview.events` topic + key `session.scored`/`session.abandoned` + queue `campaign.ranking`(E4)/`payment.credit`(E7)). Code E2/E3/E4/E7 đã khớp — chỉ chốt doc.
- **BK10 → BỎ:** BC3/BC4 (consume/release ví cá nhân B2C) = **covered-by-E7** — `InterviewEventConsumer` (E7) consume/release **generic mọi session** theo reservation (gồm B2C owner=User từ BC2); B2C Failed→release đã có ở BK12. Không cần path B2C riêng → BC3/BC4 đánh **covered-by-E7** (chỉ cần test e2e khi có broker).

## D25 — Magic-link hiện tại là ĐỦ cho FR12; KHÔNG thêm mật khẩu cho link test (2026-07-19)
**Quyết định:** **Ratify tài liệu**, không viết code — task `F23` đóng ở trạng thái *ratified, không làm*. Đường vào bài thi B2B giữ nguyên **magic-link bearer token**: 256-bit CSPRNG · lưu **hash SHA-256** (DB23, không còn plaintext trong DB) · có hạn (`expires_at`) · revoke được · single-use · reissue được (D4).
**Vì sao:** câu chữ FR12 ("link test có mật khẩu") mô tả *phương tiện*, còn thứ cần đạt là *chỉ đúng ứng viên được mời mới vào được bài*. Token hiện tại đã đạt mục tiêu đó với entropy 256-bit — thêm mật khẩu **không tăng an toàn thật** mà còn có khả năng **giảm**: mật khẩu do HR đặt sẽ ngắn, đoán được, dùng lại giữa các campaign, và **được gửi qua chính email chứa link** ⇒ kẻ đọc được email thì có cả hai, còn ứng viên thật thì thêm một bước hỏng việc. Bịt được lỗ mà token chưa bịt (email bị chuyển tiếp) là bài toán **định danh**, và hệ thống đã giải bằng hướng khác: face-verify + anti-cheat flag (SEC-1→SEC-4, D13).
**Bị loại:** (a) mật khẩu cấp-campaign do HR đặt — thêm ma sát, thêm bề mặt brute-force (phải kèm rate-limit), không thêm an toàn thật; (b) OTP qua email mỗi lần vào bài — token đã single-use, OTP chỉ lặp lại cùng một cơ chế qua cùng một kênh; (c) bắt ứng viên đăng ký tài khoản có mật khẩu trước khi thi — mâu thuẫn D8 (account Candidate nhẹ, passwordless) và làm rơi phễu B2B.
**Hệ quả:** sửa **con số/câu chữ trong bản FR12** (như đã làm với FR02 "5 lĩnh vực"→3), không thêm task code. Nếu hội đồng yêu cầu khớp câu chữ tuyệt đối thì mở lại `F23` — khi đó **bắt buộc kèm rate-limit** đường nhập mật khẩu, nếu không sẽ tự tạo ra lỗ mà bản không-mật-khẩu vốn không có.
