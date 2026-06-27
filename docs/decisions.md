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

## D9 — Tiêu chí campaign: text → CÓ CẤU TRÚC (AI đề xuất + HR duyệt) (2026-06-24)
**Vì sao:** pipeline chấm cần tiêu chí có `weight`/`max_score` và `answer_scores.criterion_id` là FK → **không chấm được trên text thô**. Materialize thành `rubric_criteria(campaign_id)` → chấm như rubric thường + ranking có trọng số.
**Bị loại:** chấm thẳng trên `criteria_text` (lệch cấu trúc dữ liệu).

## D10 — Ranking event-driven (`SessionScored` → read-model) (2026-06-25)
**Vì sao:** tránh gọi HTTP xuyên service mỗi lần xem dashboard + tải dồn khi nhiều ứng viên xong cùng lúc; CampaignService giữ `campaign_rankings` đọc local.
**Bị loại:** HTTP pull tính ranking on-demand (timeout khi đông, recompute mỗi lần xem). Giữ 1 endpoint backfill làm fallback.

## D11 — Soft delete + Audit log (2026-06-25)
**Vì sao:** B2B có thể bị **kiện/đối chất** kết quả → cần lịch sử. `deleted_at` + purge file 90 ngày (giữ điểm/transcript). `audit_logs` ghi ai sửa gì.
**Bị loại:** hard delete (mất trắng dữ liệu pháp lý).

## D12 — `order_code` = time + random (2026-06-25)
**Vì sao:** không đoán được (chống cào) + **trong trần số của PayOS**. Đụng UNIQUE → regenerate.
**Bị loại:** auto-increment (lộ số đơn); **snowflake 64-bit (có thể vượt trần PayOS)**. ⚠ *cần verify trần orderCode PayOS.*

## D13 — Anti-cheat = FLAG cho HR, không auto-hủy (2026-06-25)
**Vì sao:** false-positive auto-hủy giết oan ứng viên thật. Tín hiệu (tab/focus/paste/multi-voice) → cảnh báo, **HR quyết**.
**Bị loại:** auto-hủy bài theo tín hiệu.

## D14 — Doc: doc là source of truth, code disposable (2026-06-24)
**Vì sao:** code trên branch còn dở, sẽ sửa theo doc; mirror code vào doc tạo doc sai/tạm.
**Hệ quả:** sửa doc về **thiết kế đúng**, không bám code dở; doc tách **theo service** trong `docs/services/`, global ở `docs/`.

## D15 — Ví B2C = personal credit account, prepaid-only (2026-06-26)
**Quyết định:** B2C trả tiền bằng **cùng** credit/PayOS/reserve→consume như B2B, chỉ khác **chủ ví là User** (cá nhân) thay vì Org. Tổng quát hóa "chủ ví": account có `owner_type` (`Org`|`User`) + `owner_id`. Personal account **chỉ Prepaid** (pack OneTime); **postpaid + hạn mức + hóa đơn vẫn CHỈ Org**.
**Vì sao:** [architecture.md](architecture.md) §4.1 đã chốt "B2C tự trả credit". Tái dùng nguyên sổ cái + reserve/consume (**D7**) → không nhân đôi máy thanh toán. Postpaid giữ org-only nên lý do **D5** (postpaid ép org/hợp đồng) không vỡ. B2C không có orchestrator → **InterviewService** gọi reserve khi tạo session (Campaign lo phần B2B).
**Bị loại:** (a) bảng `personal_credit_accounts` riêng → nhân đôi sổ cái + 2 code path; (b) "org ảo" cho mỗi candidate → bẩn bảng org, auth đẻ org rác.
**Hệ quả:** `org_credit_accounts` → `credit_accounts(owner_type, owner_id)`; internal API reserve nhận `{ ownerType, ownerId, sessionId }`. Chi tiết [services/payment.md](services/payment.md).
