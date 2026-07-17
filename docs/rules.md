# ISAS — Business Rules (toàn hệ thống)

> **Danh mục quy tắc nghiệp vụ toàn hệ thống** — mã hóa để tham chiếu (GEN/AUTH/INT/CAMP/PAY/AI/BC/SEC/DATA).
> Chi tiết thiết kế/state machine từng service xem `docs/services/<service>.md`; **vì sao** (D1–D23) xem [decisions.md](decisions.md).
> Ký hiệu: `🔜` = chưa làm / đang tới; `Dxx` = tham chiếu decision log.

## GEN — Ràng buộc chung (mọi service)
- **GEN-1** API public chỉ qua Gateway `/api/v1/<service>`; callback `/internal/*` + webhook PayOS **KHÔNG** qua gateway.
- **GEN-2** DB-per-service; **không FK xuyên service** (ref = Guid lỏng); cột snake_case; enum lưu string.
- **GEN-3** JWT validate offline bằng chung key; service **không gọi Auth lúc chạy**.
- **GEN-4** AIService **KHÔNG ghi DB** — kết quả trả qua callback (`X-Internal-Token`).
- **GEN-5** File lưu S3: lưu **key**, không lưu full URL.
- **GEN-6** Phân biệt B2B/B2C bằng `campaign_id` (null = B2C).
- **GEN-7** ✅ AIService internal-only — đã **bỏ `/ai` khỏi gateway** (2026-07-13); chỉ Interview/Campaign gọi nội bộ qua `AiService:BaseUrl` (Tailscale). *(Follow-up: thêm `X-Internal-Token` trên endpoint AIService.)*

## AUTH — Định danh & Tổ chức
- **AUTH-1** `register` → role **Candidate** mặc định.
- **AUTH-2** `register-org` → Employer + Organization + OrgAdmin.
- **AUTH-3** 3 platform role: Candidate / Employer / Admin (PlatformAdmin).
- **AUTH-4** Org-role: **OrgAdmin** (billing/mua gói/quản thành viên) vs **HrMember** (quản campaign, không billing). **HrMember = thành viên THUỘC org** (không phải platform role riêng) — mô hình 2-tầng chốt **D23**.
- **AUTH-5** JWT mang `org_id` + `org_role` khi user thuộc org. *(⚠ token KHÔNG refresh khi đổi role — BK14.)*
- **AUTH-6** ✅ HrMember gọi endpoint billing money-mutation → **403** (A4; verified live 2026-07-13). *(GET billing HrMember vẫn xem được — ratify.)*
- **AUTH-7** PlatformAdmin = endpoint admin-gated trong service sở hữu dữ liệu (không phải service riêng).
- **AUTH-8** Billing/credit + campaign gắn theo **ORG**, không theo cá nhân HR (D5).

## INT — Engine phỏng vấn (B2C & B2B)
- **INT-1** Session: `GeneratingQuestions → Ready → InProgress → Scoring → Scored` (lỗi sinh câu hỏi → `Failed`).
- **INT-2** Answer: `Uploaded → Scoring → Scored/Failed`.
- **INT-3** Tối đa 1 answer/câu; upload lại = ghi đè (reset transcript, publish lại).
- **INT-4** Chấm dần — publish job khi **upload**, không khi submit.
- **INT-5** Trong 1 phiên phỏng vấn, Candidate cần trả lời **ít nhất 1 câu**; chỉ khi Ready/InProgress.
- **INT-6** Republisher (RabbitMQ) quét mỗi 2 phút đẩy lại job kẹt.
- **INT-7** *(tạm hoãn)* Callback idempotent theo `(attempt, version)`.
- **INT-8** Nguồn tiêu chí: B2C = rubric `job_category` (+`campaign_id IS NULL`); B2B = tiêu chí campaign materialize (E1).
- **INT-9** Điểm kẹp `[0, maxScore]`, bỏ tiêu chí AI bịa, chống trùng, đủ mọi tiêu chí (thiếu → `Failed`).
- **INT-10** Điểm tổng: B2C = **trung bình cộng** tiêu chí (equal weight); B2B = **Σ điểm×weight** (ranking).
- **INT-11** Chỉ **chủ session** thao tác.
- **INT-12** Phát event `SessionScored` / `SessionAbandoned`. *(Tạm dùng và làm tiếp phần tạm dừng B2C.)*
- **INT-13** Danh tính B2B = magic-link → account Candidate nhẹ (D8).
- **INT-14/15/16** ✅ Chất lượng chấm (E9→E10→E11 xong): neo theo mức (E9), self-consistency median + `needs_review` (E10), reasoning trích transcript + chống prompt-injection + HR chốt (E11).

## CAMP — Campaign (B2B orchestrator)
- **CAMP-1** Lifecycle: `Draft → Active → Closed → Archived`.
- **CAMP-2** Sửa câu hỏi/tiêu chí **chỉ khi Draft** (Active → 409).
- **CAMP-3** Chỉ thành viên org sở hữu được sửa/xóa/xem kết quả (sau khi publish thì soft delete). Campaign đã publish muốn huỷ → cập nhật trạng thái sang **Closed** và **gửi mail lại** cho ứng viên.
- **CAMP-4** Mỗi campaign bắt buộc 1 position/vị trí.
- **CAMP-5** **JD** nhập bằng **PDF** (upload; AI đọc để sinh câu hỏi) — C11. **Tiêu chí** nhập **trực tiếp có cấu trúc** trong app (`criteria[]`: name/weight/maxScore/description, Σweight=1) — công ty/HR tự khai (`source=HrEdited`), **publish không cần AI** — C12. *(Không dùng template file / PDF cho tiêu chí.)*
- **CAMP-8** ✅ Distribution membership (D1–D4): invitation → join → my-campaigns → **Start** → create-or-get session (session tạo khi Start, không khi mở link); resume tới submit (D3); reissue token (D4). *(✅ **D5**: `InvitationEmailConsumer` đã build (branch `feat/b2b-email-anticheat`) tiêu thụ queue → SMTP gửi magic-link; cần SMTP creds thật để gửi live.)*
- **CAMP-9** Tôn trọng `max_candidates`.
- **CAMP-10** ✅ Ranking event-driven (E4): `SessionScored` → upsert `campaign_rankings` theo `session_id` (idempotent), đọc local.
- **CAMP-11** ✅ Pass/fail theo ngưỡng Employer `pass_score_pct` (E5); chỉ xếp hạng ứng viên `Scored`; export CSV (E6).
- **CAMP-12** Anti-cheat = **FLAG cho HR**, KHÔNG auto-hủy (D13).
- **CAMP-13** Soft-delete + `audit_logs` mọi mutation; purge file S3 sau 90 ngày (giữ điểm/transcript) (D11).

## PAY — Thanh toán & Credit
- **PAY-1** Credit = 1 lượt phỏng vấn AI-chấm; không metering token (D4).
- **PAY-2** Chủ ví Org (B2B) hoặc User (B2C, prepaid-only) — `owner_type`/`owner_id` (D5/D15).
- **PAY-3** Prepaid (pack) + Postpaid (chỉ Org, PlatformAdmin duyệt, `credit_limit`, hóa đơn cuối kỳ) (D6).
- **PAY-4** `Reserve → Consume (Scored) → Release`; idempotent theo `session_id` (D7).
- **PAY-5** Reserve trừ `remaining` ngay (atomic) chống double-spend; hết → **402**, không tạo session.
- **PAY-6** Ai reserve: **InterviewService reserve cho CẢ HAI dòng** khi tạo session (reserve-first, tránh orphan); owner do caller truyền — **B2B = Org** (Campaign gửi `campaign.OrgId` qua `/internal/sessions/campaign`), **B2C = User** (candidateId). Hết credit → **402, không tạo session**. *(BK14 — trước đây B2B chưa wire; consume/release vẫn theo event, lấy owner từ reservation.)*
- **PAY-7** `order_code` = time + random, ≤ 9.007.199.254.740.991 (trần PayOS, D12).
- **PAY-8** Cộng credit/tất toán **chỉ khi** webhook Paid + verify HMAC; idempotent theo `payos_order_code`; + active-polling đối soát.
- **PAY-9** `description` PayOS ≤ 25 ký tự.
- **PAY-10** Order terminal (Paid/Expired/Failed/Cancelled) **bất biến**; webhook muộn sau Expired → đối soát tay, không tự cộng.
- **PAY-11** Reservation Consumed/Released = absorbing; event ra ngoài thứ tự → bỏ qua (không trừ/hoàn oan).
- **PAY-12** Đình chỉ (Suspended) → chặn hành động tương lai, **không văng người đang thi**.
- **PAY-13** 1 credit = 1 lượt **được AI chấm** (PAY-1). Session đóng mà **KHÔNG answer nào đạt `Scored`** (mọi answer `Failed`/`Skipped`, `scoredCount==0`) → **KHÔNG consume**: đóng session sang `SessionAbandoned` + phát `SessionAbandoned` (E7 **release** reservation), **không** phát `SessionScored`. Áp cả 2 điểm đóng session (callback chấm dần `AnswerService` + nhánh đóng-ngay `PracticeService.SubmitSession`). Đường ≥1 answer `Scored` giữ nguyên (Scored + consume). *(B2C: candidate không bị trừ credit ví khi cả buổi lỗi chấm.)*

## AI — Độ tin cậy
- **AI-1** Sinh câu hỏi ưu tiên JD > CV > JobCategory; chấm `temperature=0`.
- **AI-2** Lỗi tạm thời → nack → republish; lỗi vĩnh viễn → `/failed`.
- **AI-3** Chống ảo giác (đủ / kẹp / bỏ-bịa / chống-trùng).
- **AI-4** Chống prompt-injection: nội dung ứng viên = **dữ liệu**, không phải lệnh.
- **AI-5** AI **không được phép ghi DB**.

## BC — B2C luyện tập cá nhân
- **BC-1** Không org, ví credit cá nhân **prepaid**.
- **BC-2** ✅ Reserve ví User khi tạo session luyện (BC2); hết → 402, không tạo session.
- **BC-3** Lịch sử chỉ của chính user.
- **BC-4** Phân tích CV (feedback + khớp JD): **TÍNH PHÍ** (trừ credit), áp cả B2C lẫn B2B. *(Bỏ "miễn phí phase 1" — đảo lại D17; **chốt BK5 2026-07-12**. Còn wire reserve/consume vào BC7 = task code.)*
- **BC-5** 🔜 Tổng kết buổi (BC9) + nhận xét AI (BC10).
- **BC-6** B2C **KHÔNG** giám sát anti-cheat (luyện tập, không phải thi).
- **BC-7** Candidate có **rubric riêng theo JobCategory** — tự CRUD (`api/practice/rubrics`, **không** admin; đảo hướng BK3). Chưa khai → dùng **seed mặc định** (BC11). Scoring **ưu tiên rubric riêng** (active) else seed. Sửa = **soft-versioned** (deactivate bản cũ + thêm bản mới active, KHÔNG hard-delete — FK `answer_scores`). Điểm tổng vẫn **TB cộng** (INT-10); `weight` chỉ để hiển thị. **(BC16, 2026-07-13.)**

## SEC — Chống gian lận (B2B, 🔜)
> ✅ **Built cross-repo (reconcile 2026-07-18; banner "detection ngoài repo" 2026-07-16 = obsolete)** — SEC-1 (toggle `anti_cheat_enabled`/`face_verify_enabled` + bảng `session_flags`) + ingest (`SessionFlagController`: candidate-JWT FE path + `X-Internal-Token` AI path) + SEC-4 (surface `CampaignResultRow.Flags[]`) **merged `main`** (D13 flag-cho-HR, backend KHÔNG auto-hủy). **SEC-2/3 detection THẬT ĐÃ build:** **FE** (`isas-frontend` `master`/`feat/b2b-candidate`): `proctor.service.ts` (tab-switch/focus/paste) + `webcam-capture.ts` (`getUserMedia` → face-enroll + face-check /30s) + consent, wired `campaign-interview.ts` (B2B-only, BC-6); **AIService** `/face-verify` InsightFace `buffalo_l` (face-match) merged `main`; face-verify gate = `FaceVerifyController` `/face-enroll`+`/face-check`. **CÒN: verify LIVE e2e** (real Chrome + stack). ⚠ FE anti-cheat ở `master` (Vercel line), `origin/main` stale. Tracking: tasks `SEC1`.
- **SEC-1** Bật theo campaign (`anti_cheat_enabled` **[chỉ cột cờ có sẵn]**, `face_verify_enabled` **[field CHƯA tồn tại]**); chỉ B2B.
- **SEC-2** Face-verify gate trước bài (chụp live ↔ ảnh tham chiếu ≥ threshold); fail → soft-flag `identity_unverified` (HR duyệt) hoặc hard-block + re-issue.
- **SEC-3** Giám sát mỗi 2 phút (face_mismatch / no_face / multiple_faces) + tab-switch/focus/paste → flag, **KHÔNG auto-dừng**.
- **SEC-4** Mọi tín hiệu = **cờ + note** summarize cho HR → HR đánh giá lại (điểm/kết quả AI = gợi ý).
- **SEC-5** Biometric cần consent; thiếu snapshot ≠ gian lận; retry chống false-positive.

## DATA — Dữ liệu & tuân thủ
- **DATA-1** Soft-delete + audit cho dữ liệu B2B (đối chất/kiện) (D11).
- **DATA-2** Tránh dư thừa: lưu kết quả/event, không lưu mọi snapshot; ảnh tham chiếu 1 bản/ứng viên/campaign.
- **DATA-3** Biometric/snapshot: lưu S3 key (không lưu ảnh trong DB), có retention + purge.
