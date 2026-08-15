# ISAS — Business Rules (toàn hệ thống)

> **Danh mục quy tắc nghiệp vụ toàn hệ thống** — mã hóa để tham chiếu (GEN/AUTH/INT/CAMP/PAY/AI/BC/SEC/DATA).
> Chi tiết thiết kế/state machine từng service xem `docs/services/<service>.md`; **vì sao** (D1–D29) xem [decisions.md](decisions.md).
> Ký hiệu: `🔜` = chưa làm / đang tới; `Dxx` = tham chiếu decision log.

## GEN — Ràng buộc chung (mọi service)
- **GEN-1** API public chỉ qua Gateway `/api/v1/<service>`; callback `/internal/*` + webhook PayOS **KHÔNG** qua gateway. ⚠ **Vế "webhook PayOS" ĐANG MÂU THUẪN THỰC TẾ — chưa ai chốt** (2026-08-07): prod chỉ có **một tunnel công khai trỏ gateway**, cổng `5271` **không có đường ra Internet**, và URL đăng ký với PayOS chính là `{GW}/api/v1/payment/webhook/payos` — [progress.md](progress.md) §2026-07-18 ghi **tiền THẬT** đã đi đúng đường đó. Gateway cũng **chưa từng** có khối chặn `webhook/**` (`grep -rn "webhook" src/gateway/` = 0 hit, mọi nhánh). Nhánh **chưa merge** `docs/d24-payos-webhook-via-gateway` (`bc465aa`) ratify chiều ngược lại. 🛑 **Đừng "sửa cho đúng luật" bằng cách chặn `webhook/**`** — làm vậy khi URL bên PayOS chưa đổi sẽ giết đường thu tiền **trong im lặng** (polling P3 vẫn cộng credit nên nhìn như vẫn chạy). Xem `R6` trong [tasks.md](tasks.md) §S10. *(Vế `/internal/*` KHÔNG bị nghi ngờ — vẫn đúng, và đã có khối chặn.)*
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
- **AUTH-5** JWT mang `org_id` + `org_role` khi user thuộc org. ✅ **Ranh giới hiệu lực khi đổi quyền = ≤ 1 TTL access token (15')** — chốt BK14: access token **offline, không thu hồi được** (GEN-3), nên đổi `org_role` / gỡ khỏi org / đăng xuất **thu hồi mọi refresh token** của user → lần refresh kế nhận quyền mới (`RefreshTokenAsync` đọc lại roles + membership). Đánh đổi **có chủ đích**: quyền cũ sống thêm tối đa 15'; cần chặt hơn → **rút ngắn TTL access**, ❌ KHÔNG thêm denylist/gọi mạng vào đường validate của service khác (vi phạm GEN-3). Chi tiết: [services/auth.md](services/auth.md).
- **AUTH-9** **Refresh token xoay vòng có cửa sổ ân hạn** (`Jwt:RefreshTokenGraceSeconds`, mặc định **60s**, `0`=tắt): token vừa bị xoay còn dùng được thêm ngần đó giây — sửa đua refresh giữa **nhiều tab** (mỗi tab giữ token riêng, chung 1 phiên). Token thu hồi **thẳng tay** (đăng xuất/đổi quyền, `replaced_by` NULL) **KHÔNG** ân hạn. ⚠ Đánh đổi: thu-hồi-tức-thì là cơ chế **phát hiện token bị đánh cắp** — ân hạn làm yếu nó trong đúng cửa sổ đó → giữ NGẮN.
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
- **INT-8** Nguồn tiêu chí: B2C = rubric `job_category` (+`campaign_id IS NULL`); B2B = tiêu chí campaign materialize (E1). ⚠ Đây là rubric của **BUỔI**; tập tiêu chí gửi vào **MỖI lượt chấm** có thể **hẹp hơn** — xem **INT-18**.
- **INT-9** Điểm kẹp `[0, maxScore]`, bỏ tiêu chí AI bịa, chống trùng, đủ mọi tiêu chí (thiếu → `Failed`). *(«đủ mọi tiêu chí» = đủ những tiêu chí **ĐƯỢC GỬI VÀO** lượt chấm đó, **không phải** toàn rubric ⇒ INT-18 gửi ít hơn vẫn hợp lệ, guard không phải sửa.)*
- **INT-10** Điểm tổng: B2C = **trung bình cộng** tiêu chí (equal weight); B2B = **Σ điểm×weight** (ranking). *(Mẫu số B2C = số tiêu chí **thực sự có điểm** (`sumPct / scoredCriteriaCount`), không phải cả rubric ⇒ tiêu chí không được hỏi (INT-18) tự rơi khỏi phép tính, công thức không phải sửa.)*
- **INT-11** Chỉ **chủ session** thao tác.
- **INT-12** Phát event `SessionScored` / `SessionAbandoned`. *(Tạm dùng và làm tiếp phần tạm dừng B2C.)*
- **INT-13** Danh tính B2B = magic-link → account Candidate nhẹ (D8).
- **INT-14/15/16** ✅ Chất lượng chấm (E9→E10→E11 xong): neo theo mức (E9), self-consistency median + `needs_review` (E10), reasoning trích transcript + chống prompt-injection + HR chốt (E11).
- **INT-19** ✅ **Phỏng vấn thích ứng CÓ Ở MỌI TIER — adaptive là ENGINE, không phải quyền lợi theo gói.** Một buổi tiêu **đúng 1 credit** bất kể gói (B2C ví cá nhân PAY-1/BC-1 · B2B ví org PAY-6), nên gói **không được lấy mất chính engine mà người dùng vừa trả tiền để chạy**. Khoá ở **3 tầng độc lập** (một tầng thôi thì tầng khác vẫn tắt adaptive trong im lặng):
  1. **Dữ liệu** — mọi plan seed (`free`/`plus`/`pro`/`starter`/`business`/`enterprise`) đều `adaptive_enabled = true`; B2C đồng trần `adaptive_max_questions = 20` (= trần hệ thống). Migration `AllowAdaptiveOnEveryTier` (Payment, **thuần UPDATE 3 hàng seed, không DDL**). Khoá bằng `PlanSeedAdaptiveTests`.
  2. **Interview (B2C)** — `Adaptive:Enabled` là **SÀN**: `adaptiveEnabled = config.Enabled || (tiering && entitlement.AdaptiveEnabled)`. Gói chỉ được **CỘNG** (bật sớm cho một tier khi rollout chung còn tắt — đó là lý do `Plan.AdaptiveEnabled` vẫn là cột sống, không phải cột chết), **không được TRỪ**.
  3. **Campaign (B2B)** — bỏ chốt chặn adaptive trong `ValidateEntitledSelection`/`ValidateEntitledMutation`; HR bật adaptive cho campaign ở **mọi** gói.
  - ⚠ **Trần `0` = "gói KHÔNG khai trần riêng", KHÔNG phải "0 câu"** ⇒ rơi về trần cấu hình. Trước đó hai đường âm thầm bóp buổi mà **không lỗi, không cảnh báo, sau khi đã trừ credit**: (a) Payment sập → fallback `Free(MaxQuestions=0)` → `Math.Clamp(x, 0, 0)` = 0; (b) client map `?? 10`/`?? 3` — hằng số ma không khai ở đâu — cho mọi plan để trống cap.
  - **Trần DƯƠNG của gói vẫn thắng** (`plus`/`pro` giữ vai trò trần thật); vượt trần gói vẫn **400 TRƯỚC reserve** (PAY-5), không cắt im lặng.
  - Cần gạt kiếm tiền còn lại **không đổi**: nguồn tiền (credit vs quota tháng), grounding, self-consistency (×N lần gọi Gemini), phân tích CV/repo, roadmap, trần campaign/candidate B2B, postpaid, seats.
- **INT-17b** ✅ **Đào sâu THEO TỪNG CÂU GỐC** (`max_deep_per_question > 0`; `0` = giữ nguyên INT-17 dưới đây ⇒ kill-switch lúc chạy): sinh sẵn **N câu gốc** (`Adaptive:SeedCount`, mặc định **5**), mỗi câu gốc được đào sâu tối đa **`MaxDeepPerQuestion`** (mặc định **3**) lần theo **chuỗi nối tiếp** (câu sâu #2 sinh từ câu trả lời của #1), **xen kẽ** ngay sau câu gốc chứ không dồn ở đuôi. Áp cho **cả B2C và B2B** (B2B: bộ câu gốc = câu HR khai trong campaign ⇒ mọi ứng viên nhận **cùng bộ câu gốc + cùng trần độ sâu**). ⚠ **Cùng trần ≠ cùng số câu — CẦN TEAM CHỐT:** ngân sách buổi (`session.max_questions`) tiêu theo **thứ tự trả lời** (`AnswerService.cs:239-245`), và số câu đào sâu thực nhận còn phụ thuộc quyết định AI (`:306-310`); campaign 10 câu + trần buổi 20 chỉ có 10 khe cho 10 chuỗi (cần 30) ⇒ hai ứng viên trả lời khác thứ tự nhận **số câu và chủ đề đào sâu khác nhau**, trong khi điểm vẫn đem xếp hạng chung (CAMP-10). Điều kiện sinh đổi từ *frontier* → *`depth` câu vừa trả lời còn dưới trần*. `end` = hết **chủ đề này**, không phải hết buổi ⇒ chỉ báo hoàn tất khi **không còn câu nào chưa trả lời**. ⚠ `MaxFollowUps` (trần theo BUỔI): đường **B2C nay code tự ép về 0** ở chế độ chuỗi (`PracticeService.cs:182`) — nên **đừng để `0` trong config**, vì giá trị config chỉ còn hiệu lực ở chế độ frontier (khi kill-switch bật), mà ở đó `0` nghĩa là **KHÔNG trần** ⇒ tắt chế độ chuỗi lại ra hành vi thứ ba. Mặc định nay là **3**. ⚠ **Đường B2B CHƯA sửa:** `PracticeService.cs:405` lấy thẳng `MaxFollowUps = request.MaxFollowUps ?? 0` (không ép 0) và `CampaignService.ValidateAdaptiveCaps` không có ràng buộc chéo ⇒ HR đặt cả hai > 0 thì trần theo buổi bó chặt hơn trần theo câu (5×3=15) và hội thoại chết ở câu sâu thứ 3. Chi tiết: [services/interview.md](services/interview.md) · [services/ai.md](services/ai.md).
- **INT-17** ✅ **Phỏng vấn THÍCH ỨNG** (hybrid seed + adaptive; toggle theo session, tắt = luồng batch tĩnh cũ): sau mỗi câu trả lời, khi **MỌI câu hiện tại của buổi đã có answer** (frontier tuyến tính) + còn ngân sách (`max_questions`/`max_follow_ups`) + chưa quá `deadline` → InterviewService gọi AIService `/decide-next` (transcribe đồng bộ + Gemini) → **append 1 câu kế** (`kind` FollowUp/Clarify/NewQuestion, `generated_from_answer_id` = idempotency) hoặc **end**. **B2C** = 1 seed → hội thoại từ lượt 2; **B2B** = seed toàn bộ campaign questions (ai cũng nhận, công bằng) → câu thích ứng **bounded** ở đuôi, chấm theo **CÙNG tiêu chí campaign** (không mở tiêu chí mới → ranking so sánh được). Transcript đồng bộ = **nguồn duy nhất** → đẩy vào `ScoringJob` (worker bỏ Whisper). `/decide-next` lỗi → **degrade** về luồng tĩnh (answer đã lưu, worker transcribe async). Câu hỏi kế trả trong **response upload** (client khỏi poll).
- **INT-18** ✅ **Chấm THEO PHẠM VI CÂU HỎI** (D29): tiêu chí **CÁCH NÓI** (`rubric_criteria.scoring_scope='Always'` — giao tiếp · trôi chảy · ngữ pháp/dùng từ · thuật ngữ) chấm **mọi câu**; tiêu chí **NỘI DUNG** (`'WhenTargeted'`) **chỉ chấm khi câu hỏi nhắm tới** (nhãn `practice_questions.target_criterion_ids`, AI gắn lúc sinh câu hỏi). Tiêu chí **không ai hỏi ⇒ LOẠI khỏi điểm, KHÔNG tính 0** (tính 0 = phạt ứng viên vì thứ họ không được hỏi). *Vì sao:* trước đây mọi câu bị chấm trên **cả 7 tiêu chí bất kể hỏi gì** ⇒ câu hẹp ăn 2/5 ở tiêu chí không được hỏi; đo trên deploy: **cùng trình độ, bài hẹp ~69 còn bài "đại luận" 91–97**. Seed B2C = **42 row** (7 tiêu chí × 3 nghề × 2 ngôn ngữ) = **24 `Always` + 18 `WhenTargeted`**.
  - ⚠ **`null` ≠ `[]` — điểm sống còn:** `null` = *chưa hỏi / không đủ tin* ⇒ chấm **NGUYÊN bộ** (lùi an toàn, y như trước); `[]` = *đã hỏi và câu này không nhắm tiêu chí nội dung nào* (câu xã giao) ⇒ **chỉ tiêu chí `Always`**. Gộp hai ca này làm tính năng **vô hiệu đúng ở nhóm câu cần nó nhất**.
  - ⚠ Lọc xong mà **rỗng ⇒ trả nguyên bộ**: bộ rỗng khiến answer **không bao giờ được chấm** ⇒ buổi không đóng ⇒ **mất 1 credit** (PAY-13). Với tới được khi rubric riêng (BC16) bị sửa **giữa buổi**, hoặc rubric không có tiêu chí `Always` nào.
  - **Câu đào sâu THỪA KẾ nhãn của câu cha** — đúng **do cấu trúc**, không phải xấp xỉ cho rẻ: ở chế độ chuỗi (INT-17b, `MaxDeepPerQuestion > 0`) `new_question` **kết thúc chuỗi và không append** ⇒ câu nối thêm chỉ có thể là `follow_up`/`clarify`, luôn cùng chủ đề câu cha. Chế độ **frontier** (kill-switch) thì `new_question` tới được chỗ append ⇒ **chỉ thừa kế 2 loại đào sâu**, `new_question` để `null` (chấm đủ rubric). ⚠ Không thừa kế ⇒ tính năng gần như **không có hiệu lực** vì prod chạy chế độ chuỗi (phần lớn câu trong buổi là câu đào sâu).
  - **Mặc định `Always` = lùi an toàn có chủ đích** (chiều mặc định là *chấm thừa*, không phải *bỏ chấm* — bỏ chấm nhầm làm tiêu chí biến mất khỏi kết quả mà không ai thấy). ⇒ **B2B (tiêu chí campaign HR gõ) và rubric riêng BC16 CHƯA hưởng**: không set `ScoringScope` nên nhận DEFAULT `Always` ⇒ 0 tiêu chí `WhenTargeted` ⇒ lọc không thu hẹp gì. Task `SC2`.
  - **Con dấu `practice_sessions.scoring_scope_version`** (nullable): `1` = chấm **toàn rubric** · `2` = có câu chấm trên tập **hẹp hơn** · **`null` = "KHÔNG BIẾT"** (row có trước cột này). ⚠ **KHÔNG suy "khác phiên bản" từ `null`** — suy *khác* từ *không biết* là bịa (BK23). Cần con dấu vì thu hẹp phạm vi làm điểm **không so sánh được** với điểm cũ, mà **CAMP-10** (ranking) · **BC15** (đo cải thiện) · **F14** (mốc peer) đang đem so thẳng.
  - Chi tiết: [services/interview.md](services/interview.md).

## CAMP — Campaign (B2B orchestrator)
- **CAMP-1** Lifecycle: `Draft → Active → Closed → Archived`.
- **CAMP-2** Sửa câu hỏi/tiêu chí **chỉ khi Draft** (Active → 409).
- **CAMP-3** Chỉ thành viên org sở hữu được sửa/xóa/xem kết quả (sau khi publish thì soft delete). Campaign đã publish muốn huỷ → cập nhật trạng thái sang **Closed** và **gửi mail lại** cho ứng viên.
- **CAMP-4** Mỗi campaign bắt buộc 1 position/vị trí.
- **CAMP-5** **JD** nhập bằng **text trực tiếp** (`jdText`) **hoặc** PDF upload; gửi cả hai → **text ưu tiên, bỏ file** — C11 (commit `4425461`). AI đọc JD để sinh câu hỏi bất kể nguồn nào. *(Ratify 2026-07-18: câu cũ "JD nhập bằng PDF" lệch code từ C11 — FE B2B thực tế chỉ có textarea `jdText`, không có ô upload JD.)* **Tiêu chí** nhập **trực tiếp có cấu trúc** trong app (`criteria[]`: name/weight/maxScore/description, Σweight=1) — công ty/HR tự khai (`source=HrEdited`), **publish không cần AI** — C12. *(Không dùng template file / PDF cho tiêu chí.)* **Ngưỡng độ dài text nhập tay: `jdText`/`criteriaText` ≤ 20.000 ký tự** (đo SAU trim; vượt → **400** kèm giới hạn + độ dài đang gửi). Ngưỡng **CHUNG cho cả B2B và B2C** — hằng số dùng chung `Isas.Shared.Validation.TextInputLimits.JdTextMaxChars`, áp ở Campaign (`POST`/`PUT /campaign`) lẫn Interview (tạo session luyện · cv-analysis, guard **trước reserve credit** — PAY-5). *Vì sao 20.000:* JD thật khuyến nghị 300–700 từ (~2.000–5.000 ký tự), JD doanh nghiệp dài dòng hiếm khi quá 8.000–10.000 → ngưỡng rộng gấp ~2–4 lần, không chặn nhầm JD hợp lệ; cửa sổ ngữ cảnh **không** phải ràng buộc (gemini-2.5-flash ~1.048.576 token input, 20.000 ký tự tiếng Việt ≈ 7.000–10.000 token = dưới 1%) — ràng buộc thật là **chi phí token + bề mặt lạm dụng** vì text đi thẳng vào prompt Gemini. *(KHÔNG áp cho text trích từ PDF upload — luồng đó đã chặn bằng cỡ file ≤10MB.)*
- **CAMP-14** ✅ **Sàng CV = HR technical screener, KHÔNG phải máy chấm điểm.** Thước đo là
  `campaigns.job_needs` (nhu cầu công việc suy từ JD), **KHÔNG** phải `campaign_criteria` — đó là
  rubric chấm *câu trả lời nói* của buổi phỏng vấn ("Giao tiếp & Tiếng Anh", mức neo "1-4 điểm
  (Kém)…"), CV là giấy nên model chỉ đoán được (đo trên prod: hai ứng viên khác hẳn nhau đều nhận
  đúng 7/10 ở tiêu chí đó).
  - **Chốt MỘT LẦN cho cả campaign** (AI đề xuất lúc publish → HR sửa khi `Draft`, ngoài Draft → 409
    theo CAMP-2). Bước suy nhu cầu chỉ đọc JD nên nó là thuộc tính của vị trí; suy lại theo từng CV
    thì hai ứng viên cùng campaign bị đo bằng hai thước khác nhau rồi xếp chung bảng — đúng thứ bất
    công **CAMP-10** chặn ở đường phỏng vấn. `source` do **server sở hữu** (F10).
  - 🔴 **Điểm do CampaignService TÍNH, không nhận số nào của AI:**
    `100 × Σ(Strong=1 · Partial=0.5 · Weak=0) / số nhu cầu`. Model chỉ gán **mức** + **TRÍCH bằng
    chứng từ CV**. *Vì sao:* đo trên prod, bốn CV có bằng chứng **giống hệt nhau** nhận 70/70/55/55
    và ứng viên yếu hơn xếp trên ứng viên mạnh hơn — số holistic mâu thuẫn với chính bằng chứng model
    vừa liệt kê. Trung bình **đều** giữa 4 nhóm nhu cầu: không có dữ liệu nói technical đáng gấp mấy
    lần communication, bịa hằng số rồi trưng ra như chuẩn ngành đúng thứ **F14** đã từ chối làm.
  - **`verificationRisk`** (`Low·Medium·High`) là **cờ đứng cạnh điểm, KHÔNG nhập vào điểm** — gộp hai
    thứ khác bản chất vào một con số là làm mất khả năng giải thích nó.
  - **Không tìm thấy bằng chứng ⇒ ghi đúng câu `"Không thấy bằng chứng"`** (hằng số), không để trống:
    phải phân biệt "đã tìm và không thấy" với "quên đánh giá". Nhóm `Weak` = **việc cần hỏi ở vòng
    phỏng vấn**, không phải kết luận ứng viên không có.
  - **`verifyQuestions` (≤3) chỉ hiển thị cho HR**, ⚠ KHÔNG ghi vào `campaign_questions`: bộ câu
    campaign là bộ **CHUNG cho mọi ứng viên**, đó là nền tảng khiến xếp hạng **CAMP-10** so sánh được.
  - **Con dấu `cv_submission.screening_version`**: `1`/null = điểm cũ do LLM phán trên rubric phỏng
    vấn · `2` = tính từ bằng chứng. Hai thang **không so sánh được** — có dấu để không bị trộn im lặng
    (tiền lệ `scoring_scope_version`/BK23). Chi tiết: [services/campaign.md](services/campaign.md) ·
    [services/ai.md](services/ai.md) §Sàng CV B2B.
- **CAMP-8** ✅ Distribution membership (D1–D4): invitation → join → my-campaigns → **Start** → create-or-get session (session tạo khi Start, không khi mở link); resume tới submit (D3); reissue token (D4). *(✅ **D5**: `InvitationEmailConsumer` đã build (branch `feat/b2b-email-anticheat`) tiêu thụ queue → SMTP gửi magic-link; cần SMTP creds thật để gửi live.)*
- **CAMP-9** Tôn trọng `max_candidates`.
- **CAMP-10** ✅ Ranking event-driven (E4): `SessionScored` → upsert `campaign_rankings` theo `session_id` (idempotent), đọc local.
- **CAMP-11** ✅ Pass/fail theo ngưỡng Employer `pass_score_pct` (E5); chỉ xếp hạng ứng viên `Scored`; export CSV (E6).
- **CAMP-12** Anti-cheat = **FLAG cho HR**, KHÔNG auto-hủy (D13).
- **CAMP-13** Soft-delete + `audit_logs` mọi mutation; purge file S3 sau 90 ngày (giữ điểm/transcript) (D11).

## PAY — Thanh toán & Credit
- **PAY-1** Credit = 1 lượt phỏng vấn có bộ câu hỏi AI được materialize bền vững ở `Ready`; không metering token (D4/D26). Khi `Billing:ConsumeAtQuestionGeneration=false` (mặc định trong giai đoạn PONR3 chưa deploy), hệ thống giữ luật cũ consume lúc `Scored`.
- **PAY-2** Chủ ví Org (B2B) hoặc User (B2C, prepaid-only) — `owner_type`/`owner_id` (D5/D15).
- **PAY-3** Prepaid (pack) + Postpaid (chỉ Org, PlatformAdmin duyệt, `credit_limit`, hóa đơn cuối kỳ) (D6).
- **PAY-4** `Reserve → Consume → Release`, idempotent theo `session_id`. PONR1 bật tường minh: consume ngay sau commit `Ready` + questions; trước mốc đó (validation/AI/DB lỗi) release. `SessionAbandoned` sau `OrphanReconcile:ConsumeFromUtc` không release; R1 consume bù nếu inline consume lỗi.
- **PAY-5** Reserve trừ `remaining` ngay (atomic) chống double-spend; hết → **402**, không tạo session.
- **PAY-6** Ai reserve: **InterviewService reserve cho CẢ HAI dòng** khi tạo session (reserve-first, tránh orphan); owner do caller truyền — **B2B = Org** (Campaign gửi `campaign.OrgId` qua `/internal/sessions/campaign`), **B2C = User** (candidateId). Hết credit → **402, không tạo session**. *(BK14 — trước đây B2B chưa wire; consume/release vẫn theo event, lấy owner từ reservation.)*
- **PAY-7** `order_code` = time + random, ≤ 9.007.199.254.740.991 (trần PayOS, D12).
- **PAY-8** Cộng credit/tất toán **chỉ khi** webhook Paid + verify HMAC; idempotent theo `payos_order_code`; + active-polling đối soát.
- **PAY-9** `description` PayOS ≤ 25 ký tự.
- **PAY-10** Order terminal (Paid/Expired/Failed/Cancelled) **bất biến**; webhook muộn sau Expired → đối soát tay, không tự cộng.
- **PAY-11** Reservation Consumed/Released = absorbing; event ra ngoài thứ tự → bỏ qua (không trừ/hoàn oan).
- **PAY-12** Đình chỉ (Suspended) → chặn hành động tương lai, **không văng người đang thi**.
- **PAY-14** ✅ **Suất dùng thử B2C (F7):** ví của một **`User`** được tặng **3 credit ngay lúc TẠO ví** (`Billing:FreeTrialCredits`, `0` = tắt) — chỉ `owner_type=User`, ví **Org không có** (B2B đi ví Org, BC-1). Cấp ở **đúng một chỗ**: bên trong câu INSERT của `CreateAccountAsync`, nên phủ cả đường webhook Paid lẫn đường **tạo ví lúc reserve đầu tiên** (mới, để user chưa từng mua không còn nhận 402). Credit tặng **KHÔNG phải xô riêng** — nằm chung `remaining_credits`, tiêu theo đúng PAY-4/PAY-11/PAY-13, và mỗi lần cấp ghi **1 bút toán `FreeGrant +N`** để bất biến `remaining + reserved = Σ delta` vẫn đúng. **Ví đã tồn tại không bao giờ được top-up**; **không backfill** ví cũ. Chi tiết: [services/payment.md](services/payment.md) §Suất dùng thử B2C.
- **PAY-13** Khi PONR1 được bật sau PONR3, `Ready` là point of no return: session không có answer hay no-show vẫn đã tính 1 credit. Ngoại lệ duy nhất là lỗi trước materialize (`generation_failed`, validation, AI/DB lỗi) → release, không thu. PONR2 sẽ bổ sung hoàn có hạn mức cho B2B no-show bằng bút toán riêng; không thuộc PONR1.

## AI — Độ tin cậy
- **AI-1** Sinh câu hỏi ưu tiên JD > CV > JobCategory; chấm `temperature=0`.
- **AI-2** Lỗi tạm thời → nack → republish; lỗi vĩnh viễn → `/failed`.
- **AI-3** Chống ảo giác (đủ / kẹp / bỏ-bịa / chống-trùng).
- **AI-4** Chống prompt-injection: nội dung ứng viên = **dữ liệu**, không phải lệnh.
- **AI-5** AI **không được phép ghi DB**.
- **AI-6** ✅ **Grounding (D27):** ground **lớp SINH** (câu hỏi · lý thuyết · roadmap) vào corpus tài liệu uy tín admin-curate (kho `knowledge_sources` + Qdrant, InterviewService); AIService thêm `/embed` stateless + inject nguồn, trả `citedChunkId` **chỉ từ tập đã cấp** (chống bịa URL by-construction). **KHÔNG** ground lớp CHẤM. Không có nguồn khớp → **ungrounded** (VẪN sinh + nhãn "chưa có nguồn", KHÔNG citation giả). Embedder `gemini-embedding-001` đa ngôn ngữ. Default **TẮT** (`GROUNDING_ENABLED`). Chi tiết: [services/ai.md](services/ai.md) §Grounding · [services/interview.md](services/interview.md) §Kho tri thức.

## BC — B2C luyện tập cá nhân
- **BC-1** Không org, ví credit cá nhân **prepaid**.
- **BC-2** ✅ Reserve ví User khi tạo session luyện (BC2); hết → 402, không tạo session.
- **BC-3** Lịch sử chỉ của chính user.
- **BC-4** Phân tích CV (feedback + khớp JD): **TÍNH PHÍ** (trừ credit), áp cả B2C lẫn B2B. *(Bỏ "miễn phí phase 1" — đảo lại D17; **chốt BK5 2026-07-12**. Còn wire reserve/consume vào BC7 = task code.)*
- **BC-5** 🔜 Tổng kết buổi (BC9) + nhận xét AI (BC10).
- **BC-6** B2C **KHÔNG** giám sát anti-cheat (luyện tập, không phải thi).
- **BC-7** Candidate có **rubric riêng theo JobCategory** — tự CRUD (`api/practice/rubrics`, **không** admin; đảo hướng BK3). Chưa khai → dùng **seed mặc định** (BC11). Scoring **ưu tiên rubric riêng** (active) else seed. Sửa = **soft-versioned** (deactivate bản cũ + thêm bản mới active, KHÔNG hard-delete — FK `answer_scores`). Điểm tổng vẫn **TB cộng** (INT-10); `weight` chỉ để hiển thị. **(BC16, 2026-07-13.)**

## SEC — Chống gian lận (B2B, 🔜)
> 🟡 **Backend scaffold built, detection cross-repo (2026-07-16)** — SEC-1 (toggle `face_verify_enabled` + bảng `session_flags`) + ingest endpoint (nhận cờ từ FE/AI) + SEC-4 (surface cờ cho HR: `CampaignResultRow.Flags[]`) **ĐÃ build** (branch `feat/b2b-email-anticheat`, D13 flag-cho-HR). Scaffold chỉ **NHẬN+LƯU+SURFACE** cờ. ❌ **CÒN (ngoài repo):** SEC-2/3 **detection thật** — **FE** (webcam/tab-switch/paste) + **AIService** (face-match/multi-voice); face-verify gate. Tracking: tasks `SEC1`.
- **SEC-1** Bật theo campaign (`anti_cheat_enabled` **[chỉ cột cờ có sẵn]**, `face_verify_enabled` **[field CHƯA tồn tại]**); chỉ B2B.
- **SEC-2** Face-verify gate trước bài (chụp live ↔ ảnh tham chiếu ≥ threshold); fail → soft-flag `identity_unverified` (HR duyệt) hoặc hard-block + re-issue.
- **SEC-3** Giám sát mỗi 2 phút (face_mismatch / no_face / multiple_faces) + tab-switch/focus/paste → flag, **KHÔNG auto-dừng**.
- **SEC-4** Mọi tín hiệu = **cờ + note** summarize cho HR → HR đánh giá lại (điểm/kết quả AI = gợi ý).
- **SEC-5** Biometric cần consent; thiếu snapshot ≠ gian lận; retry chống false-positive.

## DATA — Dữ liệu & tuân thủ
- **DATA-1** Soft-delete + audit cho dữ liệu B2B (đối chất/kiện) (D11).
- **DATA-2** Tránh dư thừa: lưu kết quả/event, không lưu mọi snapshot; ảnh tham chiếu 1 bản/ứng viên/campaign.
- **DATA-3** Biometric/snapshot: lưu S3 key (không lưu ảnh trong DB), có retention + purge.
