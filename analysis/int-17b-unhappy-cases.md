# INT-17b — Multi-Agent Unhappy Case Audit

> **Đối tượng review:** commit `cc7bc2c` *"feat(interview): INT-17b — đào sâu theo TỪNG câu gốc (5 câu × tối đa 3 tầng)"* (48 file, +4541/−153), đã merge `main` qua PR #126 (`9852241`).
> **Nguồn bằng chứng:** diff + code hiện hành + `docs/rules.md` · `docs/services/interview.md` · `docs/services/ai.md` · `docs/tasks.md` · `docs/progress.md` · test .NET/pytest · repo FE `../isas-frontend` @ `a0aa178` · `deploy/compose.yaml` · `.github/workflows/ci.yml` · `DEPLOYMENT.md`.
> **Luật:** mọi kết luận truy được về `file:line`. Chỗ nào không có bằng chứng thì ghi **"Không đủ dữ liệu để xác nhận"** và liệt kê lại ở mục 11.
> **Cột `UC cũ`** map về bản audit một-lượt trước đó (UC-01…UC-31); `mới` = vòng này phát hiện.

> ### ⚠ Đo trực tiếp trên server (2026-08-02) — thay cho 3 mục "không đủ dữ liệu"
>
> | Đo được | Giá trị thật |
> |---|---|
> | `interviewservice-main` env | `Adaptive__Enabled=true` · `Adaptive__SeedCount=1` · `Adaptive__MaxQuestions=6` · `Adaptive__MaxFollowUps=3` · **không có** `MaxDeepPerQuestion` và `MaxFailuresPerSession` (⇒ lấy appsettings = 3 và 3 ⇒ **chế độ chuỗi đang BẬT**) |
> | `aiapi` / `aiworker` env | `WHISPER_MODEL=small` (cả hai) · `USAGE_SINK_BASE` và `PROMPT_REGISTRY_BASE` **đã được đặt** |
> | Image Interview đang chạy | `ghcr.io/su26se043/isas.interviewservice:main`, build `2026-08-01T17:59:28Z` — **sau** merge PR #126 (`17:53:26Z`) |
> | DB `isas_interview` | **thiếu cả 4 cột INT-17b**; head = `20260801064732_AddTrackBDatabaseHardening` |
> | DB `isas_campaign` | **thiếu `campaigns.max_deep_per_question`**; head = `20260801090000_DropDeadCampaignColumns` |
>
> ⇒ sinh **`DB-06`** (sự cố đang diễn ra, Critical) và **`CFG-07`**; `CFG-03`, `REL-01`, `PERF-06`, `DB-04` được cập nhật theo số đo thật.
>
> ✅ **`DB-06` đã được xử lý ngay trong phiên này (2026-08-02)** — apply 2 migration lên prod, backfill verified bằng phép kiểm độc lập, `GET /practice/sessions/{id}` và `GET /campaign` đều trả **200**, log sạch `42703`. Xem `DB-06` để có bằng chứng từng bước. **`CFG-07` (env `Development`) vẫn CHƯA sửa.**

---

## 1. Executive Summary

| Nhóm | Số UC | Critical | High | Chưa xử lý đầy đủ |
|---|---|---|---|---|
| Business & Semantic (BUS) | 7 | 0 | 3 | 6 |
| Adaptive Logic (LOG) | 10 | 1 *(đã xử lý)* | 1 *(đã xử lý)* | 8 |
| Ordering & Sequencing (ORD) | 5 | 0 | 0 | 5 |
| Database & Migration (DB) | 6 | 1 *(đã xử lý)* | 1 | 4 |
| Configuration & Deployment (CFG) | 7 | 0 | 4 | 5 |
| Reliability (REL) | 7 | 0 | 2 | 7 |
| Performance & Cost (PERF) | 6 | 0 | 1 | 6 |
| Test Coverage (TEST) | 9 | 0 | 1 | 8 |
| **Tổng** | **57** | **2** *(cả 2 đã xử lý)* | **13** | **49** |

> **Đã xử lý ngay trong phiên (2026-08-02):** `DB-06` (apply migration, sự cố prod) · `DB-04` (đo: 0 dòng mồ côi) · `BUS-01` + `CFG-01` + `TEST-09` (bản vá code, nhánh `fix/int17b-seed-budget-killswitch`) · `CFG-03` phần repo · `BUS-02` đỡ một phần.

**Điều nghiêm trọng nhất không nằm trong code mà nằm trên server**: `DB-06` — image mang INT-17b chạy production 9 tiếng trong khi 2 migration chưa apply, `InterviewService` và `CampaignService` trả 500 trên đường request. ✅ **Đã xử lý 2026-08-02**: apply 2 migration, backfill verified bằng phép kiểm độc lập, hai service trả 200 và log sạch `42703`. Chi tiết + bằng chứng từng bước ở `DB-06`.

**Ba điều đáng chú ý nhất trong bản thân commit** (đều là phát hiện mới, không có trong bản audit trước):

1. **Kill-switch không hoàn chỉnh** (`CFG-01`): đặt `Adaptive:MaxDeepPerQuestion = 0` **không** đưa hệ thống về hành vi trước INT-17b, vì nhánh chọn số câu gốc rẽ theo `adaptiveOn` chứ không theo trần độ sâu, và 3 giá trị mặc định khác trong `appsettings.json` cũng đã đổi trong cùng commit.
2. **Đường mặc định của B2C sinh 0 câu đào sâu** (`BUS-01`): FE đặt sẵn `questionCount = 5`, mà `MaxQuestions = 5` với 5 câu gốc thì ngân sách cạn ngay trước lời gọi AI đầu tiên.
3. **Hợp đồng dây giữa .NET và AIService không có test nào** (`TEST-01`): xoá 4 dòng mapping ở `app/main.py:311-314` thì 265 pytest vẫn xanh, và phía .NET payload là anonymous object nên `PropertyNamingPolicy = CamelCase` không áp — một lỗi gõ tên field không có gì bắt được.

---

## 2. Business & Semantic Agent (BUS)

### BUS-01 — Đường mặc định B2C sinh 5 câu gốc và **0 câu đào sâu** *(UC cũ: mới)*

**Evidence**
- FE đặt mặc định 5 câu: `../isas-frontend/src/app/features/candidate/practice/practice-list.ts:101-104` — `questionCount: [ 5, [Validators.required, …] ]`
- `questionCount` trở thành **trần buổi**: `src/services/Isas.InterviewService/Services/PracticeService.cs:178` — `: _adaptive.Enabled ? (questionCount ?? _adaptive.MaxQuestions) : 0,`
- Số câu gốc bị kẹp theo chính trần đó: `PracticeService.cs:210-213` — `Math.Max(1, session.MaxQuestions > 0 ? Math.Min(_adaptive.SeedCount, session.MaxQuestions) : _adaptive.SeedCount)`
- Ngân sách: `src/services/Isas.InterviewService/Services/AnswerService.cs:239-245` — `askedCount < session.MaxQuestions` … `if (!budgetLeft) return EndOutcome("end", pendingCount);`

**Failure**
Ứng viên giữ nguyên lựa chọn mặc định (5 câu): `MaxQuestions = 5`, `seedCount = min(5,5) = 5`, sau khi lưu seed thì `askedCount = 5` nên `5 < 5` sai ⇒ ngân sách cạn ⇒ trả về trước cả khi gọi AI. Buổi chạy đúng như luồng tĩnh cũ, không có câu đào sâu nào, và không có thông báo nào cho ứng viên. Chuỗi chỉ có ngân sách khi `questionCount > SeedCount`; khít nhất tại `questionCount = 20`.

**Severity** — High
**Status** — ✅ **ĐÃ SỬA 2026-08-02** (nhánh `fix/int17b-seed-budget-killswitch`)
**Ghi chú theo số đo 2026-08-02** — với env server lúc phát hiện (`Adaptive__SeedCount=1`, `CFG-03`) thì lỗi này **chưa cắn**: `seedCount = max(1, min(1, 5)) = 1` nên còn 4 khe ngân sách. Nó cắn ngay khi `SeedCount` được đặt về 5 như thiết kế — tức là chính lúc bật tính năng lên đúng hình dạng.

**Bản vá** — số câu gốc nay **chia** ngân sách buổi cho chiều sâu thay vì lấy thẳng `SeedCount`:
`seeds = clamp(ceil(MaxQuestions / (1 + maxDeep)), 1, SeedCount)` ⇒ trần 20 → 5 gốc (5×4 = 20) · 10 → 3 · 6 → 2 · **5 → 2 (2 gốc + 3 khe sâu = đúng 5)** · 1 → 1. Bất biến được khoá bằng test không phải "số câu gốc" mà là *"sau khi rải seed vẫn còn khe cho câu đào sâu"* — số câu gốc chỉ là phương tiện. Test `Create_ChuoiDaoSau_SoCauGoc_ChiaTheoNganSachBuoi` (4 ca `[Theory]`); mutation "bỏ ceil-div, lấy thẳng `SeedCount`" → **2 ĐỎ**.
**Cross-agent confirmation** — LOG (`AnswerService.cs:239-245`), CFG-03, PERF (số lời gọi AI = 0 ở cấu hình 5-seed)
**Recommended verification** — Unit test `CreateSessionAsync(questionCount: 5)` + 1 upload → assert số lời gọi decider và số câu cuối buổi; và chốt sản phẩm: `questionCount` là *tổng câu* hay *số câu gốc* trên UI.

---

### BUS-02 — Trần độ sâu không nằm trong entitlement; gói `plus` chỉ còn 5/15 khe đào sâu *(UC cũ: UC-21)*

**Evidence**
- `src/services/Isas.PaymentService/Models/Plan.cs:51` — `"plus", "Plus", 1, InterviewFunding.Metered, 30, adaptive: true, maxQ: 10, followups: 3, …`
- `src/services/Isas.InterviewService/Services/EntitlementClient.cs:36` — `Math.Clamp(features.AdaptiveMaxQuestions ?? 10, 0, 20)`
- Kẹp trần: `PracticeService.cs:176-178` — `Math.Clamp(questionCount ?? entitlement.MaxQuestions, 0, Math.Min(20, entitlement.MaxQuestions))`
- Trần độ sâu **luôn** lấy từ config toàn cục: `PracticeService.cs:158` — `var maxDeepPerQuestion = adaptiveOn ? Math.Max(0, _adaptive.MaxDeepPerQuestion) : 0;`; không có field tương ứng trong `Plan.cs:17-25`, `EntitlementSnapshot.cs:4-7`, `EntitlementClient.cs:14-15`

**Failure**
Gói `plus` (trả phí, rank 1) nhận trần buổi ≤ 10: 5 câu gốc + 5 khe đào sâu cho 5 chuỗi, phân bổ theo thứ tự trả lời ⇒ các câu gốc trả lời sau không được đào sâu. Đồng thời không có cách nào bán "độ sâu" theo gói vì `MaxDeepPerQuestion` không thuộc entitlement.

**Severity** — Medium
**Status** — 🟡 **Đỡ hơn nhờ bản vá `BUS-01`, chưa đóng**
- Bản vá chia ngân sách theo chiều sâu nên gói `plus` (trần 10) nay nhận **3 câu gốc × tối đa 3 tầng** thay vì 5 câu gốc tranh nhau 5 khe ⇒ hết cảnh "câu gốc cuối không được đào sâu lần nào", và mỗi chuỗi có hình dạng giống nhau hơn. Khoá bằng ca `[InlineData(10, 3)]`.
- **Chưa đóng**: `MaxDeepPerQuestion` vẫn **không** thuộc entitlement (`Plan.cs:17-25`, `EntitlementSnapshot.cs:4-7` không có field nào), nên mọi tier dùng chung trần độ sâu từ config — không bán được "độ sâu" theo gói.

**Cross-agent confirmation** — BUS-01 (cùng bản vá), BUS-04 (cùng cơ chế phân bổ theo thứ tự trả lời)
**Recommended verification** — Quyết định sản phẩm: có đưa trần độ sâu vào `Plan` không; nếu có thì thêm field + snapshot như `MaxQuestions`.

---

### BUS-03 — Đường B2B không ép `MaxFollowUps = 0` ở chế độ chuỗi *(UC cũ: UC-01)*

**Evidence**
- B2C ép 0: `PracticeService.cs:182-186` — `MaxFollowUps = maxDeepPerQuestion > 0 ? 0 : …`
- B2B **không** ép: `PracticeService.cs:388-389` — `MaxFollowUps = request.MaxFollowUps ?? 0, MaxDeepPerQuestion = Math.Max(0, request.MaxDeepPerQuestion ?? 0)`
- Luật yêu cầu tường minh: `docs/rules.md:42` — *"⚠ `MaxFollowUps` (trần theo BUỔI) phải để **0** ở chế độ này… hội thoại chết ở câu sâu thứ 3"*
- Không có ràng buộc chéo: `src/services/Isas.CampaignService/Services/CampaignService.cs:1742-1758`
- Ngân sách chặn: `AnswerService.cs:242-243` — `(session.MaxFollowUps <= 0 || followUpCount < session.MaxFollowUps)`
- UI HR vẫn có ô nhập: `../isas-frontend/.../campaign-form.ts:158` — `<mat-label>Số câu AI hỏi thêm tối đa</mat-label>`

**Failure**
Campaign đã đặt `max_follow_ups = 3` từ trước, nay bật chuỗi qua API: sau 3 câu thích ứng của **toàn buổi**, mọi chuỗi còn lại dừng — chuỗi của 1–2 câu gốc đầu được đào sâu, phần còn lại không.

**Severity** — High
**Status** — Đã nhận diện nhưng chưa xử lý đầy đủ (luật viết trong `rules.md`, enforce ở B2C, bỏ trống ở B2B)
**Cross-agent confirmation** — LOG (`AnswerService.cs:242-243`), TEST-05 (không test nào phủ tổ hợp)
**Recommended verification** — Unit test `CreateCampaignSessionAsync(MaxFollowUps: 3, MaxDeepPerQuestion: 3)`; hoặc chặn tổ hợp ở `ValidateAdaptiveCaps` (400).

---

### BUS-04 — "Cùng trần độ sâu" ≠ "cùng số câu": fairness B2B *(UC cũ: UC-25)*

**Evidence**
- Khẳng định fairness: `docs/rules.md:42` — *"vẫn công bằng vì mọi ứng viên nhận cùng bộ câu gốc + cùng trần độ sâu"*; `docs/rules.md:43`; `PracticeService.cs:384-385`; `src/services/Isas.CampaignService/Models/Campaign.cs:31-32`
- Ngân sách toàn buổi tiêu theo thứ tự trả lời: `AnswerService.cs:239-245`
- Số câu sâu còn phụ thuộc quyết định AI: `AnswerService.cs:306-310` — `endsChain = decision.Action == "end" || … || (perQuestionMode && decision.Action == "new_question")`
- Trần buổi kẹp 0..20: `PracticeService.cs:892-902` (`requested ?? 0`, `0` = không trần)
- `docs/progress.md` (mục INT-17b) — *"hoặc chuỗi bị cắt theo thứ tự trả lời (lệch giữa các ứng viên — đúng thứ CAMP fairness muốn tránh)"*

**Failure**
Campaign 10 câu + `MaxQuestions = 20`: cần 30 khe đào sâu nhưng chỉ có 10, cấp cho chuỗi nào chạm trước. Hai ứng viên trả lời theo thứ tự khác nhau nhận số câu và chủ đề đào sâu khác nhau, trong khi điểm được xếp hạng chung (CAMP-10). Nhánh còn lại (`MaxQuestions` null → 0 = không trần) cho bài 40 câu — xem `PERF-02`.

**Severity** — High
**Status** — Đã nhận diện nhưng chưa xử lý đầy đủ (`progress.md` ghi "cần team chốt"; `rules.md:42` vẫn khẳng định công bằng)
**Cross-agent confirmation** — LOG, PERF-02
**Recommended verification** — Integration test: 2 session cùng campaign, thứ tự trả lời khác nhau → so tổng số câu + phân bố độ sâu; và sửa câu chữ `rules.md` theo quyết định cuối.

---

### BUS-05 — Số câu tối đa ×5 nhưng vẫn 1 credit/buổi *(UC cũ: UC-29)*

**Evidence**
- `docs/rules.md:59` — *"**PAY-1** Credit = 1 lượt phỏng vấn có bộ câu hỏi AI được materialize bền vững ở `Ready`"*
- Reserve 1 lần, payload không mang số câu: `PracticeService.cs:142-143`; `src/services/Isas.InterviewService/Services/CreditReservationClient.cs:38-42`
- Trừ cứng 1: `src/services/Isas.PaymentService/Services/CreditAccountService.cs:375-378`
- Chính code thừa nhận: `PracticeService.cs:844-848` — *"chi phí tăng TUYẾN TÍNH theo số câu … nhưng doanh thu là HẰNG SỐ 1 credit/buổi"*

**Failure** — Buổi B2C tối đa từ 4 câu lên 20 câu với cùng một credit; chi phí Whisper + Gemini + TTS tăng theo số câu.

**Severity** — Medium
**Status** — Đã nhận diện nhưng chưa xử lý đầy đủ
**Cross-agent confirmation** — PERF-02/03/04
**Recommended verification** — Đối chiếu `ai_usage_logs` (F22) một buổi mẫu trước/sau khi bật — xem `PERF-06` về điều kiện để số liệu đó tồn tại.

---

### BUS-06 — Không có luật cho tình huống "hết ngân sách mà còn câu gốc chưa trả lời" *(UC cũ: mới)*

**Evidence**
- Hành vi chỉ đặc tả trong comment code: `AnswerService.cs:237-238` + `:244-245` + `:399-402`
- Docs chỉ nói về `interviewComplete`: `docs/rules.md:42`; `docs/services/interview.md:195`
- FE khi `nextAction = null`: `../isas-frontend/.../practice-session.ts:344-346` hiện `'Đã nộp câu trả lời.'`

**Failure** — Từ thời điểm ngân sách cạn, mọi câu gốc còn lại chạy như luồng tĩnh; ứng viên và HR không có tín hiệu nào cho biết phần đào sâu đã tắt. Không có tài liệu nào mô tả đây là hành vi mong muốn.

**Severity** — Medium
**Status** — Chưa thấy đề cập
**Recommended verification** — Chốt hành vi kỳ vọng rồi ghi vào `docs/services/interview.md` §INT-17b; thêm test cho nhánh này (hiện chưa có — `TEST-03`).

---

### BUS-07 — Số thứ tự câu hiển thị đổi nghĩa khi danh sách lớn dần *(UC cũ: UC-15)*

**Evidence**
- B2C đánh số theo chỉ số mảng: `../isas-frontend/.../practice-session.html:28` — `Câu {{ i + 1 }}`
- B2B: `../isas-frontend/.../campaign-interview.html:47` — `Câu {{ currentIndex() + 1 }}/{{ questions().length }}`
- Danh sách lớn dần: `practice-session.ts:342-348` (poll `refresh()`), `campaign-interview.ts:323` (`appendAdaptiveQuestion`)

**Failure** — Câu "Câu 2" ở lượt trước có thể là "Câu 3" sau khi một câu đào sâu chèn vào giữa; mẫu số của B2B tăng từ 10 lên tới 40 trong lúc thi.

**Severity** — Low
**Status** — Chưa thấy đề cập
**Recommended verification** — Kiểm UX bằng tay trên staging khi bật chế độ chuỗi.

---

## 3. Adaptive Logic Agent (LOG)

### LOG-01 — `new_question` chỉ bị cấm bằng prompt; chuỗi chết sớm không để lại dấu vết *(UC cũ: UC-02)*

**Evidence** — `AnswerService.cs:306-310` (coi `new_question` là hết chuỗi) · `src/services/Isas.AIService/app/prompts.py` (`rules_block` chain-mode cấm `new_question`) · `gemini.py` `decide_next` chạy `temperature=0.3` · log chỉ có ở nhánh append thành công (`AnswerService.cs:352-354`).
**Failure** — Nếu mô hình vẫn chọn `new_question`, mọi chuỗi kết thúc ở tầng 0 và buổi ra đúng N câu gốc; không có log/metric nào đếm tần suất.
**Severity** — Medium · **Status** — Đã nhận diện nhưng chưa xử lý đầy đủ
**Recommended verification** — Đo tỉ lệ action trên staging (log/metric), không phải unit test.

### LOG-02 — Re-upload sau khi AI trả `end` gọi lại toàn bộ `/decide-next` *(UC cũ: mới)*

**Evidence** — Chốt idempotency chỉ bắt khi ĐÃ có câu con: `AnswerService.cs:231-234` — `AnyAsync(q => q.GeneratedFromAnswerId == answer.Id)` · nhánh `endsChain` không tạo con: `:306-310` · frontier cũ che một phần lỗ này: `git show cc7bc2c^:…AnswerService.cs:183-184`.
**Failure** — Ứng viên ghi âm lại một câu mà lượt trước AI đã trả `end`/`new_question`: hệ thống chạy lại Whisper đồng bộ + Gemini (tới 90s) cho mỗi lần thử, không giới hạn số lần.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Cross-agent confirmation** — PERF (chi phí), REL (độ trễ trong request upload)
**Recommended verification** — Test: `end` → re-upload → assert số lần gọi decider.

### LOG-03 — Câu con vẫn neo vào bản trả lời đã bị thay *(UC cũ: UC-05)*

**Evidence** — `AnswerService.cs:231-234` (không sinh lại) · INT-3 reset transcript/điểm nhưng giữ `answer.Id`: `AnswerService.cs:113-129` · `docs/services/interview.md:193`.
**Failure** — Sau khi ghi âm lại câu gốc, câu đào sâu vẫn là câu sinh từ nội dung cũ; lượt kế đưa transcript mới ghép với câu hỏi cũ vào cùng một prompt.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test upload → sinh con → re-upload nội dung khác → assert quyết định nghiệp vụ đã chốt.

### LOG-04 — `pendingCount` đọc trước lời gọi AI, dùng sau *(UC cũ: UC-06)*

**Evidence** — `AnswerService.cs:208-211` (tính) · `:310` + `:399-403` (dùng) · timeout 90s: `Program.cs:99-104`.
**Failure** — Trong cửa sổ tới 90s, một upload song song có thể đổi số câu chưa trả lời; cờ `interviewComplete` phản ánh trạng thái cũ.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Test 2 upload đồng thời trên cùng session (hiện chưa có hạ tầng — `TEST-06`).

### LOG-05 — Câu bị bỏ hoặc bị khoá hết giờ ⇒ `interviewComplete` không bao giờ bật *(UC cũ: UC-07)*

**Evidence** — `AnswerService.cs:399-403` — `complete = pendingCount == 0` · FE khoá vĩnh viễn câu hết giờ: `practice-session.ts:103-106`, `:184`.
**Failure** — Khối gợi ý nộp bài không bao giờ hiện; nút nộp vẫn có (`practice-session.html:159`) nên không chặn luồng.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Test 1 câu không trả lời + các chuỗi khác hết → assert cờ hoàn tất.

### LOG-06 — `new_question` ở câu cuối trả về hai thông báo mâu thuẫn *(UC cũ: UC-08)*

**Evidence** — `AnswerService.cs:306-310` + `:399-403` (trả nguyên `action` khi `complete`) · `../isas-frontend/src/app/core/models/enums.ts:76-77` — `new_question: 'AI chuyển sang một năng lực khác.'` / `end: 'AI đã hỏi xong — bạn có thể nộp bài.'`
**Failure** — Response mang `nextAction = "new_question"` + `nextQuestion = null` + `interviewComplete = true`: người dùng thấy "AI chuyển sang năng lực khác" mà không có câu nào mới, kèm khối mời nộp bài.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Test chain-mode + `new_question` ở câu cuối → assert action trả ra client.

### LOG-07 — Nhánh deadline trả `InterviewComplete: true` cứng, không đối xứng với nhánh ngân sách *(UC cũ: mới)*

**Evidence** — `AnswerService.cs:247-250` — `if (session.Deadline is DateTime dl && DateTime.UtcNow > dl) return new AdaptiveOutcome("end", null, InterviewComplete: true);` (kèm comment *"deadline kết thúc buổi THẬT"*) so với `:244-245` dùng `EndOutcome(…, pendingCount)`.
**Failure** — Quá hạn nhận bài thì ứng viên bị mời nộp ngay cả khi còn câu chưa trả lời. Đây là lựa chọn có chủ đích, nhưng không có test nào phủ và không có dòng nào trong docs mô tả.
**Severity** — Low · **Status** — Đã xử lý (có chủ đích, có comment) — thiếu test và thiếu doc
**Recommended verification** — Test chain-mode + `Deadline` đã qua → assert `(action="end", complete=true)`.

### LOG-08 — Entity `Added` còn trong change tracker sau khi append lỗi *(UC cũ: UC-09)*

**Evidence** — `AnswerService.cs:321-345` (Add rồi SaveChanges, cùng trong `try`) · `:359-381` (catch chỉ log + tăng bộ đếm, không `Detach`) · `:150` → `:531-533` (`TryPublishScoringJobAsync` gọi `SaveChangesAsync`, có catch riêng).
**Failure** — `SaveChanges` ở bước append thất bại ⇒ entity vẫn chờ insert ⇒ `SaveChanges` trong bước publish thử lại và hỏng theo ⇒ answer giữ `Uploaded`, `LastScoringPublishedAt` null, trong khi job chấm đã được publish ⇒ republisher đẩy lại ⇒ chấm trùng.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test: ép lỗi ở bước append → assert trạng thái answer + số job publish.

### LOG-09 — Báo "hết buổi" giữa chừng làm ứng viên nộp sớm ✔ *(UC cũ: UC-10)*

**Evidence** — `AnswerService.cs:399-403` (`EndOutcome`) · test `AdaptiveChainDepthInt17bTests.cs:199` và `:220` · commit message ghi rõ hậu quả *"mất 1 credit cho buổi làm dở"*.
**Failure** — (đã chặn) mời nộp bài khi còn 4 câu gốc chưa trả lời.
**Severity** — Critical (nếu không xử lý) · **Status** — Đã xử lý
**Recommended verification** — Không cần thêm nếu proposal đã phủ.

### LOG-10 — Bỏ điều kiện frontier làm hở idempotency ✔ *(UC cũ: UC-12)*

**Evidence** — `AnswerService.cs:231-234` · unique filtered index `Configurations/PracticeSessionConfiguration.cs:172-175` · test `AdaptiveChainDepthInt17bTests.cs:323`.
**Failure** — (đã chặn) một answer sinh nhiều câu con khi re-upload.
**Severity** — High (nếu không xử lý) · **Status** — Đã xử lý — chỉ dưới re-upload **tuần tự**
**Cross-agent confirmation** — TEST-06 (không có test đồng thời; chốt này nay là chốt duy nhất)
**Recommended verification** — Test 2 upload đồng thời cùng `questionId`.

---

## 4. Ordering & Sequencing Agent (ORD)

### ORD-01 — Màn transcript của HR hiển thị `orderNo` thô ⇒ số câu nhảy cóc *(UC cũ: UC-13)*

**Evidence**
- Khẳng định trong tài liệu: `PracticeService.cs:869-872` và `docs/tasks.md:443` — *"**không màn nào hiện `order_no`**"*
- Thực tế: `../isas-frontend/src/app/features/employer/campaigns/session-transcript-dialog.ts:78` — `<span class="q-no">Câu {{ q.orderNo }}</span>`
- Nguồn dữ liệu: `src/services/Isas.CampaignService/DTOs/CampaignResultsDtos.cs:101` ← `PracticeService.cs:915` (qua `GetSessionAnswersInternalAsync:730-753`)

**Failure** — Với `stride = 4`, HR mở drill-down thấy "Câu 1, Câu 2, Câu 5, Câu 9, Câu 13" — số không liên tục, dễ đọc thành "thiếu câu".
**Severity** — Medium · **Status** — Chưa thấy đề cập (tài liệu khẳng định ngược lại)
**Recommended verification** — Test FE cho `session-transcript-dialog` với session chain-mode; hoặc đổi sang đánh số theo chỉ số mảng như hai màn ứng viên.

### ORD-02 — "Kill-switch chỉnh được lúc chạy" chỉ đúng cho session MỚI; sửa tay thì đụng unique index *(UC cũ: UC-14)*

**Evidence**
- Khẳng định: `docs/services/interview.md:393` — *"0 = chế độ cũ ⇒ **kill-switch chỉnh được lúc chạy, không cần deploy lại**"*
- Không có đường ghi nào ngoài lúc tạo: `PracticeService.cs:189` (B2C), `:389` (B2B); `grep "SetProperty(s => s.MaxDeepPerQuestion…"` → 0 kết quả; không có `HttpPatch`/`HttpPut` nào của Interview đụng session
- Stride tính lúc tạo (`PracticeService.cs:293`, `:393`, `:878`) nhưng câu con lấy `question.OrderNo + 1` lúc chạy (`AnswerService.cs:316-317`), dưới unique `(session_id, order_no)` (`PracticeSessionConfiguration.cs:166`)
- Dữ liệu chế độ cũ có `Depth > 0` nhưng `RootQuestionId = null`: `AnswerService.cs:334-335` (`Depth` ghi vô điều kiện, `RootQuestionId` chỉ ghi ở chế độ chuỗi); truy vấn câu gốc dùng `RootQuestionId == null` (`:266-268`)

**Failure** — Muốn tắt/bật cho buổi đang chạy thì chỉ còn `UPDATE` SQL tay. Bật cho session stride-1: câu con nhận `order_no` trùng câu gốc kế → vi phạm unique → rơi vào catch (`AnswerService.cs:359`) → không append + tăng `adaptive_failures`, không lỗi nào trả ra client. Thêm nữa, chuỗi cũ có `root_question_id = NULL` sẽ bị đếm là câu gốc.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Cross-agent confirmation** — CFG-01, DB
**Recommended verification** — Sửa câu chữ `interview.md:393` (kill-switch áp cho session mới); nếu cần tắt cho buổi đang chạy thì phải có endpoint/script kèm test.

### ORD-03 — FE B2B `advance()` là `i + 1` mù ⇒ có thể dừng ở câu đã trả lời *(UC cũ: mới)*

**Evidence** — `../isas-frontend/.../campaign-interview.ts:205-206` (`currentIndex` = chỉ số câu chưa trả lời đầu tiên khi hydrate) · `:355-366` — `this.currentIndex.update((i) => i + 1);` · `:99-101` (mảng sắp lại theo `orderNo` mỗi lần đổi).
**Failure** — Sau khi bỏ qua một câu rồi tải lại trang, con trỏ nhảy tới câu chưa trả lời đầu tiên; các lần `advance()` sau đó có thể dừng ở câu đã trả lời (recorder hiện lại, upload lại sẽ ghi đè theo INT-3 và không sinh câu con mới).
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — vitest cho `campaign-interview`: hydrate với mảng answered không liên tục → assert `advance()` bỏ qua câu đã trả lời.

### ORD-04 — FE B2C lệch chỉ số recorder khi có câu bị khoá *(UC cũ: mới)*

**Evidence** — Recorder chỉ render cho câu `!answer && !isLocked` (`practice-session.html:64`, `:116`, `:120-137`) nhưng `currentRecorder()` tính chỉ số trên `filter((q) => !q.answer)` (`practice-session.ts:135-138`), tập này còn chứa câu đã khoá (`:103-106`); nhánh tự nộp khi hết giờ dùng chỉ số đó (`:171-175`).
**Failure** — Sau khi một câu bị khoá vì hết giờ, `this.recorders()[idx]` lệch một bậc; danh sách dài hơn (tới 20 câu) làm tình huống này xảy ra nhiều hơn.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — vitest: khoá câu 1 → hết giờ câu 2 → assert recorder được gọi đúng câu.

### ORD-05 — Comment FE nói `/start` chỉ trả seed, thực tế trả cả câu đào sâu *(UC cũ: mới)*

**Evidence** — `../isas-frontend/.../campaign-interview.ts:188` (comment) · resume đọc **mọi** câu của session: `PracticeService.cs:475-478` + `:490` · Campaign chuyển tiếp nguyên vẹn: `CampaignSessionClient.cs:106-108`, `ParticipationService.cs:279-287`.
**Failure** — Hành vi đúng; chỉ có ghi chú trong code sai, dễ dẫn người sửa sau tới kết luận ngược.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Không cần test; sửa comment.

---

## 5. Database & Migration Agent (DB)

### DB-01 — Backfill recursive CTE không có test nào phủ, và repo không có hạ tầng test migration *(UC cũ: UC-16)*

**Evidence**
- `src/services/Isas.InterviewService/Migrations/20260801174000_AddQuestionChainDepthInt17b.cs:41-70` — comment tự nêu: *"SQLite/`EnsureCreated` (bộ test) KHÔNG BAO GIỜ chạy migration ⇒ câu SQL này không có test nào phủ"*
- Hạ tầng test: `Isas.InterviewService.Tests/TestDb.cs:33-35` (`SqliteConnection(":memory:")` + `EnsureCreated()`); grep `Testcontainers` / `Migrate()` / `Respawn` toàn repo → **0 kết quả**
- Npgsql chỉ dùng offline để sinh SQL: `XminConcurrencyTokenTests.cs:19`, `ListPagingTests.cs:531`, `SweeperIndexTests.cs:132`
- `docs/tasks.md:443` — *"2 migration … **chưa apply DB thật**"*; `docs/progress.md` INT-17b — *"L3 chưa chạy"*

**Failure** — Nếu CTE sai, mọi câu đã đào sâu của buổi đang chạy giữ `depth = 0` ⇒ bị coi là câu gốc ⇒ được đào thêm tối đa 3 tầng nữa, vượt trần trong im lặng (đúng hậu quả migration tự mô tả). Không có test nào chạy được câu SQL này.
**Severity** — High · **Status** — Đã nhận diện nhưng chưa verify
**Cross-agent confirmation** — TEST (không có hạ tầng migration test)
**Recommended verification** — Migration test trên Postgres throwaway: seed cây 3 tầng + 1 seed đơn + 1 câu mồ côi → `Up` → assert `depth`/`root_question_id` từng dòng → `Down` → assert không mất dữ liệu khác. *(Quy mô bảng nhỏ: `docs/progress.md` 2026-08-01 ghi `practice_questions` 143 dòng lúc apply migration grounding ⇒ thời gian khoá không đáng kể.)*

### DB-02 — Rollback migration khi image mới đang chạy *(UC cũ: UC-17)*

**Evidence** — `Down()` drop 4 cột: migration `:79-100` · code mới đọc chúng: `AnswerService.cs:179`, `:192`, `:222`, `PracticeService.cs:189` · tiền lệ trong repo: `docs/progress.md` 2026-08-01 (`DropDeadCampaignColumns` ⇒ *"mọi request đọc campaign sẽ 500 (`42703 column does not exist`)"*).
**Failure** — Chiều apply-migration-rồi-deploy an toàn (cột additive). Chiều ngược lại làm mọi truy vấn `practice_questions`/`practice_sessions` lỗi `42703`.
**Severity** — Medium · **Status** — Chưa thấy đề cập (tài liệu chỉ nói "reversible")
**Recommended verification** — Ghi thứ tự bắt buộc vào runbook apply/rollback.

### DB-03 — Không có CHECK cho `max_deep_per_question`; đường internal bỏ qua trần 3 của Campaign *(UC cũ: UC-22)*

**Evidence** — `PracticeService.cs:389` — `Math.Max(0, request.MaxDeepPerQuestion ?? 0)` (chỉ chặn số âm) · trần 3 chỉ ở Campaign: `CampaignService.cs:1734` · CHECK hiện có: `PracticeSessionConfiguration.cs:42-43` (chỉ `max_questions` và `status`) · tiền lệ tự nêu trong code (F2b): *"trần ở tầng service chặn được đường HTTP, nhưng đường internal thì không → chốt thêm ở DB"*.
**Failure** — `POST /internal/sessions/campaign` nhận giá trị bất kỳ ≥ 0; stride và độ sâu giãn theo, chỉ còn `MaxQuestions` chặn — mà `0` nghĩa là không trần (`PracticeService.cs:892-902`).
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test gọi thẳng endpoint internal với `maxDeepPerQuestion = 100`; hoặc thêm CHECK ở DB như `max_questions`.

### DB-04 — Câu hỏi mồ côi `generated_from_answer_id` bị coi là câu gốc *(UC cũ: UC-18)*

**Evidence** — CTE join `practice_answers` (`migration:58-62`); dòng không join được không nằm trong `chain` nên giữ `depth = 0`, `root_question_id = NULL` (`WHERE q.id = chain.id …`) · chế độ chuỗi coi `RootQuestionId == null` là câu gốc (`AnswerService.cs:266-268`).
**Failure** — Nếu tồn tại dòng như vậy, nó vào danh sách `otherTopics` và được đào thêm tối đa 3 tầng.
**Severity** — Low · **Status** — ✅ **Đã đo và loại 2026-08-02**: câu đếm cho **0** dòng mồ côi trên prod, nên backfill phủ trọn 40 câu adaptive. Rủi ro vẫn còn về mặt cấu trúc (CTE sẽ bỏ qua dòng mồ côi nếu sau này có).
**Recommended verification** — Đã chạy: `SELECT count(*) FROM practice_questions q LEFT JOIN practice_answers a ON a.id = q.generated_from_answer_id WHERE q.generated_from_answer_id IS NOT NULL AND a.id IS NULL;` → `0`

### DB-05 — Migration Campaign DROP rồi ADD lại CHECK *(UC cũ: UC-19)*

**Evidence** — `src/services/Isas.CampaignService/Migrations/20260801174011_AddCampaignMaxDeepPerQuestionInt17b.cs:13-26`.
**Failure** — `ADD CHECK` quét toàn bảng dưới `ACCESS EXCLUSIVE`. Vế mới chỉ nói về cột vừa tạo (toàn NULL) nên không thể fail dữ liệu; chi phí là thời gian khoá, tỉ lệ thuận với số dòng `campaigns` (không có số liệu trong repo).
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Đếm số dòng `campaigns` trong preflight apply-window.

### DB-06 — 🔴 SỰ CỐ ĐANG DIỄN RA: image có INT-17b, DB chưa có migration *(UC cũ: mới)*

**Evidence** *(đo trên server 2026-08-02)*
- Image đang chạy build **sau** merge: `docker inspect interviewservice-main` → `ghcr.io/su26se043/isas.interviewservice:main`, built `2026-08-01T17:59:28Z`; merge PR #126 (`9852241`) = `2026-08-01T17:53:26Z`
- Xác nhận **bằng hành vi**, không bằng dấu thời gian (bài học `OPS1`): log chứa `SELECT p.id, p.adaptive_enabled, p.adaptive_failures, … p.max_deep_per_question, …` ⇒ model trong image có 4 cột mới
- DB thiếu cả 4 cột: `information_schema.columns` cho `practice_sessions` (`max_deep_per_question`, `adaptive_failures`) và `practice_questions` (`depth`, `root_question_id`) → **rỗng**; `__EFMigrationsHistory` head = `20260801064732_AddTrackBDatabaseHardening`
- Lỗi thật: `Npgsql.PostgresException (0x80004005): 42703: column p.adaptive_failures does not exist`, gần nhất `2026-08-02T01:29:52Z`
- **Đường REQUEST chứ không phải job nền**: câu hỏng là `FROM practice_sessions AS p WHERE p.id = @sessionId LIMIT 1`, theo sau là `fail: Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware[1]`
- Campaign hỏng song song: `campaigns.max_deep_per_question` **không tồn tại** (count = 0), head = `DropDeadCampaignColumns`; log `campaignservice-main` có **12×** `column c0.max_deep_per_question does not exist`

**Failure**
Mọi đường nạp **nguyên entity** `PracticeSession` (upload answer — `AnswerService.cs:57`; tạo session — `PracticeService.cs:161-190` INSERT gồm cả cột mới) và mọi đường nạp entity `Campaign` đều ném `42703` ⇒ 500. Các truy vấn dùng `.Select(...)` projection không chạm cột mới nên vẫn chạy — vì thế sự cố **không** làm service chết hẳn và không lộ ra ở `/health`, chỉ làm hỏng đúng các luồng nghiệp vụ chính. Đây chính là kịch bản `DB-02` nhưng theo chiều ngược lại (deploy code trước, migration chưa chạy) và **đang xảy ra**.

**Severity** — Critical
**Status** — ✅ **ĐÃ XỬ LÝ 2026-08-02** (trước đó: chưa thấy đề cập; `docs/tasks.md:443` vẫn ghi *"2 migration … chưa apply DB thật"*)
**Cross-agent confirmation** — DB-02 (cùng lớp), CFG-02 (cùng nhóm lệch nhịp deploy), CFG-07 (stack trace bị lộ ra ngoài cùng lúc)

**Đã làm — apply lên prod, kèm bằng chứng từng bước**
1. **Preflight read-only**: `practice_questions=151`, `practice_sessions=96`, `practice_answers=98`, **câu mồ côi = 0** (đóng luôn `DB-04`), 40 câu adaptive sẽ được backfill; `campaigns=58`, constraint `ck_campaigns_adaptive_caps_non_negative` tồn tại, 0 dòng vi phạm CHECK mới.
2. **Sinh idempotent script** từ repo (`dotnet ef migrations script --idempotent`), **đọc bằng mắt** trước khi chạy (tiền lệ `AddAuditColumnsAndTypes` thiếu `;`): CTE kết thúc `;` đúng chỗ trong `DO $EF$`, toàn bộ nằm trong một `START TRANSACTION … COMMIT`.
3. **Apply** `docker exec -i postgres-main psql -v ON_ERROR_STOP=1` → cả hai `COMMIT`, exit 0. Head mới: `20260801174000_AddQuestionChainDepthInt17b` · `20260801174011_AddCampaignMaxDeepPerQuestionInt17b`.
4. **Verify backfill bằng phép kiểm ĐỘC LẬP với chính CTE** (đối chiếu quan hệ cha–con trực tiếp qua `generated_from_answer_id` → `practice_answers.question_id`): seed sai = **0** · con thiếu root = **0** · `depth ≠ cha+1` hoặc `root ≠ coalesce(cha.root, cha.id)` = **0** · root trỏ vào thứ không phải seed = **0** · tổng câu **151 không đổi**. Phân bố: `depth 0=111 · 1=22 · 2=10 · 3=8` (40 câu con, khớp preflight).
5. **Verify tầng app** (không tin dấu thời gian — `OPS1`): `GET /api/v1/interview/practice/sessions/{id}` → **200** (đúng hình dạng câu từng hỏng: nạp nguyên entity theo id) · `GET /api/v1/campaign` → **200** · log 2 phút sau: 1 truy vấn chạm `adaptive_failures`, 1 chạm `max_deep_per_question`, **0 dòng `42703`**, 0 restart.

> 💡 **Vì sao backfill là bắt buộc, nay có số liệu**: 8 câu đang ở `depth 3`. Bỏ backfill thì cả 8 nhận `depth = 0` ⇒ bị coi là câu gốc ⇒ được đào thêm 3 tầng nữa, vượt trần trong im lặng — đúng hậu quả migration tự mô tả.

---

## 6. Configuration & Deployment Agent (CFG)

### CFG-01 — Kill-switch KHÔNG đưa hệ thống về hành vi trước INT-17b *(UC cũ: mới)*

**Evidence**
- Nhánh chọn số câu gốc rẽ theo `adaptiveOn`, **không** theo trần độ sâu: `PracticeService.cs:210-213` — `int? seedCount = adaptiveOn ? Math.Max(1, …) : null;` · `:219` — `var requestedCount = seedCount ?? questionCount;`
- Bản cũ xin AI theo `questionCount`: `git show cc7bc2c^:…PracticeService.cs` (`GenerateQuestionsAsync(..., questionCount, ...)`) và chọn overload theo `questionCount is not null`
- Cắt seed đổi công thức: cũ `generated.Take(Math.Max(1, _adaptive.SeedCount))` → mới `PracticeService.cs:284` — `seedCount is int sc ? generated.Take(sc)`
- Cùng commit đổi thêm 3 mặc định: `appsettings.json:45-52` (`SeedCount 1→5`, `MaxQuestions 10→20`, `MaxFollowUps 3→0`) so với `cc7bc2c^`
- Các nhánh CÓ revert đúng (đã đối chiếu từng dòng với `cc7bc2c^`): `AnswerService.cs:186`, `:219`, `:222`, `:244-245`, `:249-250`, `:254-256`, `:264-273`, `:306-310`, `:316-320`, `:367-382`; `PracticeService.cs:182-186`, `:293/:393` (`SeedOrderStride:878` → stride 1)

**Failure** — Đặt `Adaptive__MaxDeepPerQuestion=0` cho ra "5 câu gốc, 0 câu đào sâu, trần buổi 20, trần follow-up 0" — không phải hành vi cũ ("1 seed + tối đa 3 câu thích ứng/buổi"). Muốn về đúng cũ phải sửa **4 khoá**. Trong sự cố cần tắt gấp, đây là đường thoát không hoạt động như tài liệu mô tả.
**Severity** — High · **Status** — ✅ **ĐÃ SỬA 2026-08-02** (nhánh `fix/int17b-seed-budget-killswitch`)

**Bản vá — 2 phần, vì kill-switch hỏng ở 2 tầng khác nhau**
1. **Code**: nhánh `seedCount`/`requestedCount` nay rẽ theo **trần độ sâu** thay vì `adaptiveOn`, và thêm lại nhánh `Take(Math.Max(1, SeedCount))` cho ca "adaptive bật + trần 0" ⇒ đặt `MaxDeepPerQuestion = 0` trả lại **đúng** đường trước INT-17b: xin AI `questionCount`, đi overload 4 tham số, cắt seed theo `SeedCount`.
2. **Config**: `MaxFollowUps` mặc định `0 → 3` (cả `appsettings.json` lẫn `AdaptiveOptions`). Ở chế độ chuỗi code đã tự ép về 0 nên giá trị này chỉ còn hiệu lực **đúng lúc kill-switch bật** — mà `0` ở đó nghĩa là *không trần*, tức tắt chế độ chuỗi lại ra một hành vi thứ ba (thích ứng chạy tới tận trần buổi) thay vì hành vi cũ.

⇒ **kill-switch nay là MỘT khoá** (`Adaptive__MaxDeepPerQuestion=0`), không phải bốn. Test `Create_KillSwitch_TranDoSauBang0_QuayLaiDungLuongTruocInt17b` dùng `MockBehavior.Strict` và chỉ setup overload 4 tham số, nên gọi nhầm overload là đỏ ngay. Mutation: rẽ lại theo `adaptiveOn` → **1 ĐỎ** · bỏ nhánh `Take` → **1 ĐỎ** · mặc định `MaxFollowUps` về 0 → **1 ĐỎ** (chỉ sau khi thêm test khoá mặc định — xem `TEST-09`).
**Cross-agent confirmation** — BUS-01 (cùng nhánh `seedCount`), ORD-02, TEST-08

### CFG-02 — Lệch phiên bản deploy: AIService không nằm trong CI, pydantic nuốt 4 field im lặng *(UC cũ: UC-03)*

**Evidence**
- CI chỉ build 5 image .NET: `.github/workflows/ci.yml:51-89`
- `DEPLOYMENT.md:475` — *"**CI không build AIService** — Mac build tay (`up -d --build`), không pull GHCR"*; `DEPLOYMENT.md:402-419` (aiapi + aiworker dùng chung `isas.aiservice:local`)
- `deploy/compose.yaml:9-10` — *"AIService runs SEPARATELY on the Mac"*
- Cảnh báo trong chính code: `AiServiceInterviewDecider.cs:50-52` và `app/schemas.py` — *"pydantic `extra='ignore'` sẽ NUỐT IM LẶNG field quên khai"*
- Hệ quả nối tiếp: `AnswerService.cs:308` coi `new_question` là hết chuỗi

**Failure** — Deploy Interview mới trước khi rebuild `isas.aiservice:local`: 4 field bị bỏ đi ⇒ AIService sinh prompt chế độ cũ (còn chào `new_question`) trong khi .NET đang ở chế độ chuỗi ⇒ chuỗi chết sớm. Không lỗi, không log, không khác biệt mã trạng thái.
**Severity** — High · **Status** — Chưa thấy đề cập (cảnh báo hiện có chỉ nói về việc *quên khai field*, không nói về lệch phiên bản)
**Cross-agent confirmation** — TEST-01 (không test nào bắt), REL
**Recommended verification** — Contract test qua route (`client.post("/api/v1/decide-next", …)` với JSON camelCase) + bước verify sau deploy: gọi `/decide-next` với `maxDepth=3` và assert prompt/response phản ánh chế độ chuỗi.

### CFG-03 — Env production đè 3 khoá, thiếu khoá thứ 4 ⇒ hình dạng chạy thật là "1 câu gốc × 3 tầng" *(UC cũ: UC-20)*

**Evidence** *(đã đo, không còn là suy đoán)*
- `docker inspect interviewservice-main` → `Adaptive__Enabled=true`, `Adaptive__SeedCount=1`, `Adaptive__MaxQuestions=6`, `Adaptive__MaxFollowUps=3`; **không có** `Adaptive__MaxDeepPerQuestion` lẫn `Adaptive__MaxFailuresPerSession`
- Thiếu env ⇒ lấy `appsettings.json:50-51` — `"MaxDeepPerQuestion": 3`, `"MaxFailuresPerSession": 3` ⇒ `PracticeService.cs:158` cho `maxDeepPerQuestion = 3` ⇒ **chế độ chuỗi đang bật**
- Nhưng `MaxFollowUps = 3` (env) bị `PracticeService.cs:182-183` ép về **0** vì `maxDeepPerQuestion > 0` ⇒ khoá env này hiện vô hiệu ở B2C
- Repo vẫn không quản 4 khoá này: `deploy/compose.yaml:104-125` không có `Adaptive__*` nào; `.env.example`, `DEPLOYMENT.md`, `.github/workflows/`, `scripts/`, `Makefile`, `render.yaml` — 0 kết quả; `.gitignore:26-30` loại mọi `.env*`

**Failure**
Hình dạng chạy thật là **1 câu gốc, đào sâu tối đa 3 tầng, trần buổi 6** — không phải "5 câu gốc × 3 tầng" mà commit thiết kế. Cấu hình này đến từ ba khoá env viết cho INT-17 (2026-07-18) cộng một mặc định mới không ai đặt; không có file nào trong repo mô tả tổ hợp đó, nên nó chỉ đọc được bằng cách `docker inspect` trên máy chủ.

**Severity** — High
**Status** — 🟡 **Đã sửa phần REPO, còn phần SERVER** (nhánh `fix/int17b-seed-budget-killswitch`)
- ✅ Cả **6** khoá `Adaptive__*` nay nằm trong `deploy/compose.yaml` + `.env.example` với mặc định an toàn (`ADAPTIVE_ENABLED=false`), kèm ghi chú kill-switch là khoá nào ⇒ hết cảnh cấu hình chạy thật chỉ đọc được bằng `docker inspect`.
- ⏳ **Chưa** đổi env trên server, và **cố ý**: FE luôn gửi `questionCount` (mặc định 5) đè `MaxQuestions`, nên đặt `SeedCount=5` khi image chưa mang bản vá `BUS-01` sẽ ra 5 câu gốc + 0 khe đào sâu — tệ hơn hiện trạng. Thứ tự bắt buộc: **merge → CI build image → deploy → mới đổi env**.
- Hiện trạng trong lúc chờ vẫn chạy được: `SeedCount=1` + trần độ sâu 3 ⇒ "1 câu gốc × 3 tầng".

**Cross-agent confirmation** — CFG-01 (cùng bản vá), BUS-01 (là lý do của thứ tự deploy)
**Recommended verification** — Sau khi image mới lên: `docker inspect interviewservice-main | grep Adaptive` đối chiếu 6 khoá, rồi tạo 1 buổi B2C với `questionCount = 20` và assert ra 5 câu gốc.

### CFG-04 — B2B không bật được chế độ chuỗi từ giao diện *(UC cũ: UC-27)*

**Evidence** — `grep -rn "maxDeepPerQuestion" ../isas-frontend/src` → **0 kết quả** · form HR chỉ có 2 ô: `campaign-form.ts:159`, `:164`, `:479-480` · BE đã lộ field: `CampaignResponse.cs:166`, `:192` · `docs/progress.md` INT-17b — *"Đề xuất: mặc định trần đào sâu cho B2B là 1, **HR tự nâng**"*.
**Failure** — Chỉ bật được qua API/SQL. *(Điểm giảm nhẹ đã kiểm: `CampaignService.cs:339-346` chỉ ghi đè khi `HasValue`, nên FE PUT không mang field sẽ không xoá giá trị đã đặt bằng API.)*
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Nếu bật B2B: thêm control + vitest; nếu không: ghi rõ "API-only" trong `docs/services/campaign.md`.

### CFG-05 — `Internal:Token` rỗng làm adaptive tắt câm và đốt luôn cầu dao *(UC cũ: mới)*

**Evidence** — `AiServiceInterviewDecider.cs:27` (`_internalToken = config["Internal:Token"]`, kiểu `string?`) · `:63` — `msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);` (không guard) · AIService fail-closed: `app/main.py:31-40`, `:270-271` → 401 · 401 gộp vào `AiServiceException` (`AiServiceInterviewDecider.cs:76-81`) → nuốt ở `AnswerService.cs:359-363` → tăng `adaptive_failures` (`:371-375`) → cầu dao đóng sau 3 lần (`:196-202`). Không có startup validation nào cho khoá này.
**Failure** — Sai/thiếu một biến môi trường làm tính năng tắt hoàn toàn với triệu chứng duy nhất là `LogWarning`; sau 3 lượt thì cả buổi ngừng gọi AI, không phân biệt được với sự cố upstream.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Guard lúc khởi động (fail-fast nếu `Internal:Token` rỗng) + tách log cho 401 khỏi log lỗi upstream.

### CFG-06 — `MaxFailuresPerSession = 0` tắt cầu dao ✔ *(UC cũ: UC-23)*

**Evidence** — `AnswerService.cs:186` — `if (perQuestionMode && _adaptive.MaxFailuresPerSession > 0)` · mặc định 3: `Models/AdaptiveOptions.cs:34`, `appsettings.json:51` · `docs/services/interview.md:197` chỉ mô tả "chạm trần → thôi gọi".
**Failure** — Đặt `0` thì không đọc bộ đếm, mọi lượt tiếp tục chờ hết timeout khi AIService hỏng.
**Severity** — Low · **Status** — Đã xử lý (điều kiện `> 0` tường minh trong code); ngữ nghĩa `0` chưa ghi vào tài liệu
**Recommended verification** — Ghi ngữ nghĩa `0` vào `docs/services/interview.md`.

### CFG-07 — 5/5 container production chạy `ASPNETCORE_ENVIRONMENT=Development` (lần thứ ba) *(UC cũ: mới)*

**Evidence** *(đo trên server 2026-08-02)*
- `docker inspect` từng container: `gateway-main`, `authservice-main`, `interviewservice-main`, `campaignservice-main`, `paymentservice-main` — **cả 5** đều `ASPNETCORE_ENVIRONMENT=Development`
- Hậu quả quan sát được trong log: `fail: Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware[1]` ngay sau lỗi `42703` của `DB-06`
- Tiền lệ: `docs/progress.md` 2026-07-18 (*"5/5 container prod chạy `ASPNETCORE_ENVIRONMENT=Development` → request ẩn danh nhận full stack trace"*) và 2026-07-20 (*"đúng bug 18/07 tưởng đã sửa, tức quay lại LẦN 2"*, sinh `OPS6` để sửa tận gốc trong compose version-control)

**Failure**
Mọi lỗi trả về trang exception của Development: stack trace, đường dẫn build, và — với `DB-06` đang xảy ra — **cả câu SQL kèm tên cột** cho bất kỳ ai gọi được endpoint. Việc này quay lại lần thứ ba vì bản sửa hai lần trước là `sed` trên compose của server chứ không phải trong repo (`OPS6` vẫn chưa làm).

**Severity** — High
**Status** — Đã nhận diện nhưng chưa xử lý đầy đủ (`OPS6`)
**Cross-agent confirmation** — DB-06 (khuếch đại hậu quả: lỗi schema bị phơi ra ngoài)
**Recommended verification** — Đặt `ASPNETCORE_ENVIRONMENT=Production` **trong `deploy/compose.yaml`** (không sửa tay trên server), rồi ép 1 lỗi và assert response không chứa stack trace.

---

## 7. Reliability Agent (REL)

### REL-01 — Độ trễ `/decide-next` đồng bộ so với timeout 90s, nay gọi 15 lần/buổi *(UC cũ: UC-28)*

**Evidence**
- `Program.cs:99-104` (timeout 90s) · số lời gọi 3 → 15 (xem `PERF-02`) · Whisper chạy trong chính request: `app/main.py:285-289`
- **Đo trên server 2026-08-02:** `aiapi` và `aiworker` đều `WHISPER_MODEL=small` — **không phải** `large-v3`
- Số 64–96s trong `docs/progress.md` 2026-07-20 (`OPS4`) là đo với `large-v3`; `docs/tasks.md:443` gọi `OPS4` là "chặn cứng" dựa trên số đó

**Failure**
Tiền đề của `OPS4` đã đổi: model hiện tại là `small`, nên rủi ro "≈1/3 lượt vượt 90s" **không còn được số liệu nào chống đỡ** — nhưng cũng chưa có số đo nào cho `small` dưới chế độ chuỗi. Phần cấu trúc vẫn đứng: mỗi lượt upload chờ đồng bộ, 15 lượt/buổi, không retry, và 3 lần quá hạn là đủ đóng cầu dao cho cả buổi.

**Severity** — Medium *(hạ từ High sau khi đo: `large-v3` không còn chạy)*
**Status** — Đã nhận diện nhưng chưa xử lý đầy đủ — số đo cũ đã lỗi thời, số đo mới chưa có
**Cross-agent confirmation** — PERF-02, REL-02
**Recommended verification** — Đo p95 `/decide-next` với `WHISPER_MODEL=small` trên một buổi 20 câu trước khi mở rộng; cập nhật lại `OPS4` theo số mới.

### REL-02 — AIService không có timeout phía server cho Whisper *(UC cũ: mới)*

**Evidence** — `app/main.py:285-289` — `await asyncio.to_thread(transcriber.transcribe_detailed, tmp_path, req.language)` không bọc `asyncio.wait_for` · `app/transcriber.py:41-46` gọi `self._model.transcribe(...)` không giới hạn thời gian · Dockerfile CMD không đặt request timeout · grep `wait_for|timeout` trong `app/` chỉ ra `prompt_registry.py:102`, `usage.py:159`, `config.py:83,98` (không liên quan).
**Failure** — Khi client .NET hết 90s và bỏ đi, luồng Whisper vẫn chạy tới hết trên máy AIService, giữ CPU mà không còn ai nhận kết quả. Chế độ chuỗi gọi sau gần như mọi câu trả lời nên các lượt bỏ dở chồng lên nhau trên một tiến trình.
**Severity** — High · **Status** — Chưa thấy đề cập
**Recommended verification** — Test tải trên staging: bắn N request `/decide-next` rồi huỷ ở giây 90 → quan sát CPU/độ trễ của lượt kế; thêm `asyncio.wait_for` phía server.

### REL-03 — Gemini lỗi làm mất transcript vừa tốn CPU để tạo *(UC cũ: mới)*

**Evidence** — `app/main.py:285-289` (transcribe thành công) → `:316-317` — `except Exception as ex: raise HTTPException(status_code=502, detail=f"Lỗi quyết định câu hỏi kế: {ex}")`; thân 502 không mang `transcript` · .NET chỉ lưu transcript khi có kết quả: `AnswerService.cs:290-296` · worker sẽ Whisper lại: `app/worker.py:118-133`.
**Failure** — Một lỗi Gemini (quá tải/rate limit) huỷ luôn phần transcribe đã chạy; hệ thống trả tiền CPU hai lần cho cùng một câu trả lời.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Cross-agent confirmation** — PERF
**Recommended verification** — Test AIService: mock Gemini ném lỗi sau khi transcribe → assert response còn mang transcript (hoặc .NET lưu transcript trước khi quyết định).

### REL-04 — Cầu dao chỉ có một chiều: không reset, không giảm khi thành công, không half-open *(UC cũ: mới)*

**Evidence** — Chỗ ghi duy nhất là tăng: `AnswerService.cs:371-375`; grep `adaptive_failures|AdaptiveFailures` toàn repo → chỉ có migration, configuration, entity và dòng tăng đó · đọc: `:186-202`.
**Failure** — Ba lỗi rải rác trong một buổi 20 câu (kể cả do sự cố thoáng qua) tắt phần thích ứng cho toàn bộ phần còn lại; không có đường phục hồi nào ngoài `UPDATE` SQL tay.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test: 2 lỗi + 1 thành công + 1 lỗi → assert hành vi mong muốn (đặt lại bộ đếm hay không) sau khi chốt thiết kế.

### REL-05 — Cầu dao đếm mọi exception, và chế độ cũ không có cầu dao *(UC cũ: UC-04)*

**Evidence** — `catch (Exception ex)` bọc toàn bộ khối (từ `pendingCount` tới `SaveChanges` append): `AnswerService.cs:359` · bộ đếm nằm trong catch đó: `:367-375` · cả cổng chặn lẫn bộ đếm đều gói trong `if (perQuestionMode …)`: `:186`, `:367`.
**Failure** — 3 lỗi DB (kể cả vi phạm unique ở `ORD-02`) tắt phần thích ứng dù AIService khoẻ, và log ghi *"Adaptive decide-next lỗi"* cho lỗi không phải decide-next. Ngược lại, buổi chạy chế độ cũ không có cầu dao nào nên vẫn chờ hết timeout mỗi lượt.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test: ném lỗi DB (không phải decider) 3 lần → assert bộ đếm/cổng theo thiết kế đã chốt.

### REL-06 — Không có retry ở bất kỳ HttpClient nào; mọi lỗi gộp thành một loại *(UC cũ: mới)*

**Evidence** — grep `Polly|AddStandardResilienceHandler|AddPolicyHandler|CircuitBreaker|WaitAndRetry` toàn repo (`*.cs`, `*.csproj`, `*.props`, `*.json`) → **0 kết quả** · `AiServiceInterviewDecider.cs:65-95`: `HttpRequestException`/`TaskCanceledException`, non-2xx, `JsonException`, thiếu `action` — tất cả thành `AiServiceException` không phân biệt.
**Failure** — Một lỗi mạng thoáng qua tiêu một "mạng" của cầu dao y như một sự cố thật; 401 (sai cấu hình) và 502 (upstream) không phân biệt được từ log lẫn từ bộ đếm.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Quyết định có retry hay không (chú ý: retry nằm trong request upload đồng bộ, cộng thêm vào 90s); tách loại lỗi trước khi đếm.

### REL-07 — Quyết định adaptive thất bại thì không bao giờ được thử lại *(UC cũ: mới)*

**Evidence** — `StuckAnswerRepublisher.cs:76-85` (predicate), `:93`, `:155` (mang transcript) · grep `IAiServiceInterviewDecider|_decider` → không có hit nào trong `StuckAnswerRepublisher.cs`.
**Failure** — Answer có lượt adaptive hỏng được chấm lại, nhưng câu đào sâu tương ứng mất vĩnh viễn (chốt `generated_from_answer_id` cũng không giúp vì chưa từng có câu con).
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Chốt xem có cần retry đường quyết định không; nếu có thì cần khoá idempotency riêng.

---

## 8. Performance & Cost Agent (PERF)

> Mọi con số dưới đây suy từ hằng số được trích dẫn: `AdaptiveOptions.cs:15` `SeedCount = 5`, `:19` `MaxQuestions = 20`, `:29` `MaxDeepPerQuestion = 3` (khớp `appsettings.json:47-51`), so với bản `cc7bc2c^` (`SeedCount = 1`, `MaxFollowUps = 3`).

### PERF-01 — Round-trip DB mỗi buổi ~69 → ~350 *(UC cũ: mới)*

**Evidence** — Đường upload đầy đủ ở bản mới đếm được **20** round-trip (`AnswerService.cs:57`, `:71`, `:77-82` (split ⇒ 2), `:136`, `:192`, `:208`, `:231`, `:239`, `:240`, `:266`, `:295`, `:341`, `:430`, `:441`, `:533` + 2 lần resolve rubric qua `:562`/`:567` ở cả nhánh adaptive lẫn publish); bản cũ **19** (`cc7bc2c^` cùng đường, có thêm `maxOrder` `:233`, thiếu `:192` và `:266`). Số **lượt** đi hết đường mới là phần đổi: 15 lượt gọi AI + 5 lượt chạm trần, so với 3 + 1 ở bản cũ.
**Failure** — Tải DB cho một buổi B2C tăng khoảng 5 lần; các query mới (`:192` cầu dao, `:266` nạp toàn bộ câu gốc) chạy trên **mọi** lượt upload.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Đo p95 thời gian `POST /answers` trên staging với buổi 20 câu.

### PERF-02 — Lời gọi AI đồng bộ mỗi buổi 3 → 15 *(UC cũ: UC-28 phần chi phí)*

**Evidence** — Điều kiện gọi: `AnswerService.cs:222` (gọi khi `Depth < MaxDeepPerQuestion`) + `:242-243`; `MaxFollowUps` bị ép 0 ở B2C nên vế thứ hai luôn đúng (`PracticeService.cs:182-184`). 5 chuỗi × 3 tầng = **15**; lời gọi thứ 15 xảy ra khi `askedCount = 19 < 20`. Bản cũ: 3 lời gọi rồi `followUpCount = 3 >= 3` ⇒ dừng.
**Failure** — Mỗi lời gọi nằm **trong** request upload, gồm cả Whisper (`app/main.py:285-289`), timeout 90s. Đây là nhân tố nhân lên cho REL-01, REL-02 và BUS-05.
**Severity** — High · **Status** — Đã nhận diện nhưng chưa xử lý đầy đủ
**Cross-agent confirmation** — REL-01, REL-02, BUS-04
**Recommended verification** — Đo tổng thời gian một buổi 20 câu trên staging trước khi bật.

### PERF-03 — Job chấm mỗi buổi 4 → 20 (gói `pro`: 12 → 60) *(UC cũ: UC-29)*

**Evidence** — `AnswerService.cs:501-503` (`n` job/answer) · mặc định `SelfConsistencyN = 1`: `Models/ScoringOptions.cs:15`, `Entities/PracticeSession.cs:74` · gói `pro` đặt `scn: 3`: `Plan.cs:52`, `:67`.
**Failure** — Số lượt chấm Gemini/buổi tăng 5 lần ở mọi mức `SelfConsistencyN`, trong khi doanh thu vẫn 1 credit.
**Severity** — Medium · **Status** — Đã nhận diện nhưng chưa xử lý đầy đủ
**Recommended verification** — Đối chiếu `ai_usage_logs` trước/sau (điều kiện: `PERF-06`).

### PERF-04 — Câu đào sâu không bao giờ trúng cache TTS *(UC cũ: mới, mở rộng UC-29)*

**Evidence** — Cache theo nội dung: `app/tts.py:29` — `hashlib.sha256(f"{voice}\x00{text}".encode("utf-8"))`; lý do ghi ở `tts.py:6-8` (seed B2B dùng chung) · câu đào sâu sinh riêng theo transcript từng người: `AnswerService.cs:326` · đơn giá TTS: `src/services/Isas.PaymentService/appsettings.json:50-52` — `"gemini-2.5-flash-preview-tts": { "InputPerMillionUsd": 0.50, "OutputPerMillionUsd": 10.00 }` so với `"gemini-2.5-flash"` `0.30 / 2.50`.
**Failure** — Số câu đào sâu/buổi 3 → 15 ⇒ số lượt TTS không cache được tăng 5 lần, trên đúng model có đơn giá output cao nhất.
**Severity** — Medium · **Status** — Đã nhận diện nhưng chưa xử lý đầy đủ (`docs/progress.md` ghi *"TTS cache miss ~6 → ~20/buổi"*)
**Recommended verification** — Đếm `X-Tts-Cache: miss` trên staging cho một buổi 20 câu.

### PERF-05 — Mỗi lời gọi kèm nguyên văn 5 câu gốc *(UC cũ: mới)*

**Evidence** — `AnswerService.cs:266-272` (`rootQuestion` + `otherTopics` = 4 phần tử với 5 seed) → block prompt `app/prompts.py:856-862` · đổi lại, lịch sử ngắn hơn: `BuildAdaptiveChainAsync` (`AnswerService.cs:426-451`) tối đa 2 lượt Q&A so với `BuildAdaptiveHistoryAsync` (`:453-472`) tối đa 3 lượt ở bản cũ.
**Failure** — Prompt mỗi lời gọi ngắn hơn ở phần transcript nhưng cộng thêm 5 nội dung câu hỏi; nhân với số lời gọi tăng 5 lần thì tổng token prompt mỗi buổi tăng.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Đo kích thước prompt thật (đã có tiền lệ đo offline ở F22).

### PERF-06 — Chi phí thật hiện không được ghi lại *(UC cũ: mới)*

**Evidence**
- `/decide-next` **có** đi qua chokepoint: `app/providers/gemini.py:876-877` → `:123-124` → `app/usage.py:157`
- **Đo trên server 2026-08-02:** `USAGE_SINK_BASE=http://100.64.204.33:5271` đã được đặt trên **cả** `aiapi` lẫn `aiworker` ⇒ nhánh `logger.info`-only (`config.py:79` mặc định rỗng, `usage.py:150-152`) **không** áp dụng
- Hai khoảng trống còn lại: Whisper chạy local không có token nên không xuất hiện ở đâu (`app/main.py:287-288`); `embed()` nằm ngoài chokepoint (`gemini.py:84-89`)
- Cấu hình này cũng không nằm trong `deploy/compose.yaml` (grep `usage_sink` → 0 kết quả) — cùng lớp với `CFG-03`

**Failure** — Token của `decide_next` được ghi lại, nên so sánh chi phí trước/sau là làm được. Phần **không** đo được là CPU Whisper — đúng phần tăng 5 lần và đúng phần đang là nút thắt độ trễ (`REL-02`).
**Severity** — Low *(hạ từ Medium sau khi đo: sink đã bật)*
**Status** — Đã nhận diện nhưng chưa xử lý đầy đủ (phần Whisper)
**Cross-agent confirmation** — CFG-03 (cấu hình runtime nằm ngoài version control)
**Recommended verification** — Chạy 1 buổi mẫu → assert có dòng `decide_next` trong `ai_usage_logs`; thêm đo thời gian transcribe nếu muốn theo dõi chi phí CPU.

---

## 9. Test Coverage Agent (TEST)

### TEST-01 — Hợp đồng dây .NET ↔ AIService cho 4 field mới không có test nào *(UC cũ: mới)*

**Evidence**
- Python: `tests/test_decide_next.py:341` dựng thẳng model (`DecideNextRequest(... rootQuestion="Gốc", currentDepth=2, maxDepth=3, otherTopics=["Khác"])`), `:358` gọi thẳng `provider.decide_next(...)` — **không** request nào qua route mang `rootQuestion` (6 lời gọi `client.post` ở `:171, :208, :231, :235, :243, :255`)
- Mapping ở `app/main.py:311-314` do đó không có test nào phủ
- .NET: grep `rootQuestion` trong `Isas.InterviewService.Tests/` → **0 kết quả**; payload là anonymous object (`AiServiceInterviewDecider.cs:53-56`) nên `PropertyNamingPolicy = CamelCase` (`:19`) không áp
- Precedent làm đúng đã có trong repo: `tests/test_roadmap.py:263` (post qua route, assert provider nhận đủ), `tests/test_worker_dlq.py:58` (pin hằng số phía .NET)

**Failure** — Xoá 4 dòng mapping ở `main.py` hoặc gõ sai tên field ở .NET đều không làm đỏ test nào — đúng lớp lỗi mà `BC14/focusCriteria` từng mắc và mà commit này tự nêu là muốn tránh.
**Severity** — High · **Status** — Chưa thấy đề cập
**Cross-agent confirmation** — CFG-02
**Recommended verification** — Một test Python qua route với JSON camelCase + một test .NET assert các khoá trong body gửi đi (serialize rồi so khớp tên).

### TEST-02 — Test Campaign nới lỏng đúng field mới *(UC cũ: mới)*

**Evidence** — `Isas.CampaignService.Tests/ParticipationServiceTests.cs:677` — `It.IsAny<DateTime?>(), true, 2, 8, It.IsAny<int?>(), …`; `:699` (nhánh tắt) — `false, null, null, It.IsAny<int?>(), …`; comment ngay trên khối khẳng định *"Start PHẢI truyền toggle + trần adaptive của campaign xuống session client"*.
**Failure** — Chính test có nhiệm vụ khoá việc chuyển trần xuống Interview lại để `maxDeepPerQuestion` là `It.IsAny` ⇒ nếu Campaign ngừng gửi (hoặc gửi sai) thì test vẫn xanh.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Thay `It.IsAny<int?>()` bằng giá trị mong đợi ở cả hai nhánh.

### TEST-03 — Ba tình huống trung tâm của chế độ chuỗi không có test *(UC cũ: một phần UC-31)*

**Evidence** — `AdaptiveChainDepthInt17bTests.cs`: `TraLoiLechThuTu_MocHaiChuoiDocLap:168` chạy tuần tự và cả hai chuỗi dừng ở depth 1; grep `Deadline` trong file → 0 hit; `ChainSession` cố định `maxQuestions = 20` (`:62`, comment `:60` — *"maxQuestions rộng để không vô tình bó trước trần độ sâu"*). Test ngân sách duy nhất là chế độ cũ: `AdaptiveInterviewTests.cs:189`.
**Failure** — Không test nào phủ: (a) hai chuỗi mọc quá depth 1 (nơi các khoảng trống `order_no` mới va nhau), (b) session quá deadline, (c) ngân sách cạn khi còn câu gốc chưa trả lời — mà (c) chính là kịch bản "mất 1 credit cho buổi làm dở" mà commit nói là đang sửa.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Ba test tương ứng; riêng (c) assert `nextAction == null` và `interviewComplete == false`.

### TEST-04 — Đường B2B tạo session ở chế độ chuỗi không có test *(UC cũ: UC-30)*

**Evidence** — grep `MaxDeepPerQuestion` trong `Isas.InterviewService.Tests` → chỉ `AdaptiveChainDepthInt17bTests` (dựng session bằng tay, `:68`) và `PracticeServiceTests` (`:143`, `:198`, `:247` — đều là B2C); `CampaignSessionTests.cs` không truyền field này.
**Failure** — Không có gì khoá việc `CreateCampaignSessionAsync` đánh số câu gốc theo stride — đúng đường mang `BUS-03`, `BUS-04`, `DB-03`.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test `CreateCampaignSessionAsync(maxDeepPerQuestion: 3, questions: 3)` → assert `order_no` = 1, 5, 9.

### TEST-05 — Không test tổ hợp `MaxFollowUps > 0` + chế độ chuỗi *(UC cũ: UC-31)*

**Evidence** — `AdaptiveChainDepthInt17bTests.cs:67` — `s.MaxFollowUps = 0;   // trần buổi TẮT — nếu để 3 nó bó chặt hơn trần theo câu`; `PracticeServiceTests.cs:169` chỉ assert đường B2C ép 0.
**Failure** — Tổ hợp mà `rules.md` và chính comment test gọi là nguy hiểm không có test nào chạy qua, trong khi đường B2B đến được nó (`BUS-03`).
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test chuỗi với `MaxFollowUps = 3, MaxDeepPerQuestion = 3, 5 câu gốc`.

### TEST-06 — Không có test đồng thời ở bất kỳ đâu trong `Isas.InterviewService.Tests` *(UC cũ: mới)*

**Evidence** — grep `Task.WhenAll|Parallel.For|new Thread|ConcurrentBag|Task.Run|SemaphoreSlim` trong project → **0 kết quả**; `XminConcurrencyTokenTests.cs:26` chỉ assert metadata model. Trong khi đó `AdaptiveChainDepthInt17bTests.cs:321` ghi rõ *"bỏ frontier rồi thì `generated_from_answer_id` là chốt duy nhất"*.
**Failure** — Chốt idempotency duy nhất chỉ được kiểm dưới re-upload tuần tự; hai upload đồng thời cùng `questionId` (đọc-rồi-ghi) không có test — cùng lớp với `LOG-04`, `LOG-08`.
**Severity** — Medium · **Status** — Chưa thấy đề cập
**Recommended verification** — Test 2 upload song song cùng session/câu → assert đúng 1 câu con.

### TEST-07 — Test "prompt cũ nguyên văn" chỉ so 4 chuỗi con *(UC cũ: mới)*

**Evidence** — `tests/test_decide_next.py:328-338` — assert `'"new_question"' in prompt`, `"Đã hỏi: 2 câu" in prompt`, `"---CHỦ ĐỀ ĐANG ĐÀO SÂU" not in prompt`, `"tầng" not in prompt`; không có golden/snapshot. *(Kiểm thực nghiệm: prompt sinh ra với `max_depth <= 0` hiện đúng là byte-identical với bản `cc7bc2c^`, kể cả khi vẫn nhận `rootQuestion`/`otherTopics`.)*
**Failure** — Một sửa đổi ở `intro`/`rules_block`/`history_label` vẫn thoả 4 assert này ⇒ nhánh "kill-switch" của prompt có thể trôi mà không ai biết.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Snapshot test so toàn văn prompt legacy.

### TEST-08 — Commit message ghi công một sửa lỗi không có trong diff *(UC cũ: mới)*

**Evidence** — Commit message: *"Kèm: sửa bom hẹn giờ có sẵn trong ParticipationServiceTests (deadline ghi cứng 2026-08-01…)"*; `git diff cc7bc2c^ cc7bc2c -- …/ParticipationServiceTests.cs` không có thay đổi ngày nào (8 hunk đều là `It.IsAny<int?>()`); `docs/progress.md:183` xác nhận bản của `main` (PR #124) mới là bản được lấy khi rebase.
**Failure** — Người đọc lịch sử tin rằng vòng này đã xử lý quả bom hẹn giờ đó; thực tế nó đến từ nhánh khác.
**Severity** — Low · **Status** — Chưa thấy đề cập
**Recommended verification** — Không cần test; sửa ghi chép khi tổng kết.

### TEST-09 — Không có test nào phủ GIÁ TRỊ MẶC ĐỊNH của `AdaptiveOptions` *(UC cũ: mới — phát hiện khi chạy mutation cho bản vá `CFG-01`)*

**Evidence**
- Mọi test adaptive tự dựng options tường minh (`PracticeServiceTests.cs:143`, `:198`; `AdaptiveChainDepthInt17bTests.cs:60-68`) ⇒ giá trị mặc định trong `Models/AdaptiveOptions.cs` không nằm trong đường đi của test nào
- Đo thật: mutation đổi mặc định `MaxFollowUps` từ 3 về 0 chạy qua **XANH 514/514**; chỉ sau khi thêm `AdaptiveOptions_MacDinh_GiuTranBuoiChoCheDoFrontier` mới **ĐỎ**

**Failure**
Mặc định C# là thứ có hiệu lực khi cả env lẫn `appsettings.json` đều vắng khoá — đúng ca "dựng service ở môi trường mới rồi bật kill-switch". Với `MaxFollowUps = 0` (nghĩa là *không trần* ở chế độ frontier), kill-switch sẽ ra hành vi thứ ba thay vì hành vi trước INT-17b, và không test nào kêu. Cùng lớp với `CFG-06`: ngữ nghĩa "0 = tắt/không trần" nằm rải rác và không được khoá ở đâu.

**Severity** — Low
**Status** — ✅ **Đã sửa 2026-08-02** — thêm test khoá cả 6 mặc định (`Enabled`, `SeedCount`, `MaxQuestions`, `MaxFollowUps`, `MaxDeepPerQuestion`, `MaxFailuresPerSession`)
**Recommended verification** — Không cần thêm.

---

## 10. Top 5 cần xử lý trước

| # | ID | Tác động | Vì sao chặn trước |
|---|---|---|---|
| ~~0~~ | ~~**DB-06**~~ | ✅ **Đã xử lý 2026-08-02** — apply 2 migration, backfill verified, hai service trả 200, log sạch `42703` | Giữ lại trong bảng để thấy thứ tự ưu tiên tại thời điểm phát hiện. |
| 1 | **CFG-07** | 5/5 container production chạy `ASPNETCORE_ENVIRONMENT=Development` ⇒ mọi lỗi trả ra ngoài kèm stack trace **và câu SQL** | Lần **thứ ba** (18/07, 20/07, nay) vì hai lần trước chỉ `sed` trên server chứ không sửa trong `deploy/compose.yaml` (`OPS6`). Sự cố `DB-06` vừa rồi phơi nguyên tên cột và câu SQL ra ngoài trong 9 tiếng. |
| ~~2~~ | ~~**CFG-01**~~ | ✅ **Đã sửa 2026-08-02** — kill-switch nay là MỘT khoá; kèm `BUS-01` (số câu gốc chia theo ngân sách) và `CFG-03` phần repo. Mutation 4/4 ĐỎ, 1597 test xanh. Còn: đổi env trên server **sau** khi image mới lên. | |
| 3 | **CFG-02 + TEST-01** | AIService build tay ngoài CI + pydantic `extra='ignore'` + không test nào phủ mapping ⇒ deploy lệch nhịp làm chuỗi chết sớm, hoàn toàn im lặng | Hai lỗ hổng trên cùng một đường: lỗi xảy ra được **và** không có gì bắt được. Một contract test qua route + một test payload .NET là việc nhỏ, chặn cả lớp lỗi đã từng làm `focusCriteria` hỏng nhiều tuần. `DB-06` cho thấy deploy ở đây thật sự lệch nhịp được. |
| 4 | **DB-01** | Backfill CTE nay đã chạy trên prod **và đúng** (verified), nhưng repo vẫn không có hạ tầng test migration Postgres | Lần này thoát nhờ dữ liệu nhỏ và kiểm tay. Migration sau sẽ không may như vậy: `EnsureCreated` trên SQLite không bao giờ chạy migration, nên mọi backfill/raw SQL vẫn ra production mà chưa từng được thực thi ở đâu. |
| 5 | **BUS-03 + BUS-04** | B2B không ép `MaxFollowUps = 0` ⇒ chuỗi chết sau 3 câu; ngân sách buổi tiêu theo thứ tự trả lời ⇒ ứng viên cùng campaign nhận số câu khác nhau | Chỉ chặn nhánh **B2B** — B2C bật được trước. Nhưng `rules.md:42` đang khẳng định "vẫn công bằng" trong khi code không bảo đảm điều đó, và kết quả B2B thì đem đi xếp hạng. |

> **Còn lại ở mức High, không vào Top 5:** `BUS-01` (mặc định `questionCount = 5` ⇒ 0 câu đào sâu — hiện *chưa* cắn vì env server đặt `SeedCount = 1`, sẽ cắn ngay khi đổi sang 5 như thiết kế) · `CFG-03` (env production là "1 câu gốc × 3 tầng", không phải "5 × 3") · `PERF-02` (15 lời gọi AI đồng bộ/buổi) · `BUS-03` + `BUS-04` — hai mục này chỉ chặn nhánh **B2B**, có thể bật B2C trước.

---

## 11. Danh sách "Không đủ dữ liệu để xác nhận"

> Ba mục của bản nháp đã được **đo trực tiếp trên server 2026-08-02** và không còn nằm ở đây: giá trị `Adaptive__*` (→ `CFG-03`), `WHISPER_MODEL` (→ `REL-01`), và việc `USAGE_SINK_BASE` có được đặt hay không (→ `PERF-06`).

| ID | Câu hỏi không trả lời được | Đã chứng minh được gì | Cần gì để chốt |
|---|---|---|---|
> `DB-04` cũng đã **đo và đóng** trong lúc preflight apply: **0** dòng `practice_questions` có `generated_from_answer_id` không khớp answer nào.

| — | Tần suất thật mô hình chọn `new_question` ở chế độ chuỗi (`LOG-01`) | Prompt cấm tường minh (`prompts.py` `rules_block`), .NET coi là hết chuỗi (`AnswerService.cs:308`); không có log/metric nào đếm | Chạy staging và đếm action |
| — | Độ trễ `/decide-next` với `WHISPER_MODEL=small` dưới chế độ chuỗi (`REL-01`) | Model đang chạy là `small` (đã đo); số 64–96s trong `docs/progress.md` là của `large-v3` nên không áp dụng | Đo p95 trên một buổi 20 câu |
| — | Số dòng bảng `campaigns` (thời gian khoá của `DB-05`) | Migration DROP+ADD CHECK (`20260801174011_…:13-26`); chưa đếm (`practice_questions` thì có: 143 dòng theo `docs/progress.md` 2026-08-01) | `SELECT count(*) FROM campaigns` trong preflight |
| — | Có bao nhiêu request thật đã nhận 500 vì `DB-06` kể từ lúc deploy | Log 12 dòng `PostgresException 42703` trong cửa sổ `--since 12h`, gần nhất `01:29:52Z`, kèm `DeveloperExceptionPageMiddleware`; log cũ hơn đã bị xoay vòng | Đếm ở tầng gateway/access-log nếu cần con số đầy đủ |
| — | Chi phí CPU Whisper (`PERF-06`) | `decide_next` có vào `ai_usage_logs` (sink đã bật); Whisper chạy local không có token nên không xuất hiện ở đâu (`app/main.py:287-288`) | Thêm đo thời gian transcribe nếu muốn theo dõi |
