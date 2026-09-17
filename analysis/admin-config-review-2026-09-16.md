# Màn cấu hình Admin — rà theo góc nhìn admin + đề xuất thiết kế (2026-09-16)

> **Đối tượng review:** 8 màn `/admin/*` của FE React `isas-web-client` @ `origin/dev` `e006734` (phần admin không đổi so nhánh đang checkout) đối chiếu với ~50 endpoint admin ở BE `isas-server` @ `main` (đọc trên `fix/money-status-flow`, phần admin không đổi).
> **Nguồn bằng chứng:** đọc code thật FE + BE (file:line ở từng dòng), `docs/progress.md` (việc admin đã phải làm bằng curl/SQL 2 tháng qua), Context7 `/dotnet/aspnetcore.docs` + `/dotnet/docs` cho 2 điểm dựa vào hành vi thư viện. Mọi khẳng định của agent quét đều được kiểm lại tay trước khi ghi.
> **Trạng thái:** phân tích chốt 2026-09-16 sáng; **đợt A (FE-only) ĐÃ LÀM cùng ngày** — xem mục *Đợt A đã làm* cuối file. Đợt B–D vẫn là đề xuất chờ duyệt. Bản gốc: `~/.claude/plans/calm-knitting-salamander.md`.
> **Kết luận một dòng:** không phải "UI xấu" — màn Rubric **không lưu được gì** (hợp đồng FE tự bịa), màn Prompt **không hiện bản đang chạy**, và **~35 endpoint admin BE đã có mà FE = 0** nên admin đang vận hành bằng curl.

## Context (vì sao làm)

User: *"cấu hình hiện tại rất lỏ — không hiểu cách sử dụng, không hiện cách cấu hình; đứng ở góc admin xem họ cần gì, phân tích kỹ."*

Admin trong ISAS = PlatformAdmin (AUTH-3/AUTH-7). Nav admin hiện có 8 mục: Bảng điều khiển · Thanh toán tổ chức · Người dùng · Tổ chức · Quản lý chiến dịch · **Cấu hình prompt** · **Duyệt rubric** · **Ngưỡng đạt lộ trình**. Ba mục cuối là "cấu hình hành vi AI" — đúng chỗ user chê. Cả ba viết ngày 2026-08-20 (`1049a76`, tokudosan12) và chỉ được L3 bằng "trang tải không 400" (2026-09-12), **chưa ai dùng thật để cấu hình** — bộ mốc rubric lên prod 2026-09-14 đi thẳng bằng API, bỏ qua UI.

## Phát hiện đã xác minh (đọc code, có file:line)

### A. `/admin/rubrics` — KHÔNG HOẠT ĐỘNG, không phải chỉ khó dùng
Hợp đồng FE viết theo tưởng tượng, không theo DTO BE (`src/services/Isas.InterviewService/DTOs/AdminRubric.cs`):

| FE đọc/gửi (`adminApi.types.ts:19-22`) | BE thật | Hệ quả |
|---|---|---|
| `level.description` | `AdminRubricLevelItem.Descriptor` → JSON `descriptor` | **mọi ô bảng trống** dù DB có mốc (prod: 6 bộ × 36 mốc) |
| `criterion.key` | `AdminRubricCriterionItem.Id` (Guid) | `key={undefined}` (warning đã ghi 09-12); `update()` so `item.key !== criterion.key` → undefined===undefined → **gõ 1 ô sửa CẢ 7 tiêu chí** |
| PUT gửi `{category, language, version, criteria[…levels[{score, description}]]}` | `UpsertAdminRubricRequest{Criteria[{Id, Description, Levels[{Score, Descriptor}]}]}` | `descriptor` cũ (spread từ GET) đi lên nguyên → fingerprint bằng nhau → `changed:false` → **200, không lưu gì, admin tưởng đã lưu** (`AdminB2CRubricService.cs:121-123`) |
| Cột cứng `[0..5]` (`RubricLevelsTable.tsx:7`) | mốc 2–10, bắt buộc có 0 và maxScore (CAMP-17) | gõ vào ô mà mốc chưa tồn tại → `levels.map` không khớp → **nuốt im lặng**; không thêm/xoá mốc được |
| "AI soạn nháp" → `setDraft(value)` coi là `RubricSet` | `AdminSuggestLevelsResponse{criteria[{criterionId,…}]}` | Lưu → BE `Id=Guid.Empty` → **400 "không thuộc bộ chuẩn"** — đường AI→Lưu luôn hỏng |
| Chấm thử gửi `{criterionKey, answer}` (chỉ tiêu chí **[0]**, ô `Input` 1 dòng) | `AdminRubricPreviewRequest{Question?, CustomAnswer?, Seniority?, SampleQuestionId?}` | BE bỏ field lạ → chạy với câu mẫu mặc định; **kết quả 3 bài mẫu KHÔNG render** (page không đọc `preview.data`) → tốn 1/5 lượt free, màn hình không đổi |
| `matrix` (3 nghề × 2 ngôn ngữ, `withLevelsCount`) · `history` · `sampleQuestions` · `reset` | BE trả/đã có | **fetch về không hiện / mutation không có nút** — đúng thứ BE viết ra để trả lời "còn thiếu ở đâu" (`AdminRubric.cs:70-76`) |
| `seniority` là `Input` tự gõ ("Ví dụ: Senior") | enum 4 mức | gõ sai không báo |
| Test `AdminRubricsPage.test.tsx:11` fixture dùng `description`/`key` | | test **xanh vì lý do sai** — khoá đúng cái shape sai |

### B. `/admin/prompts` — chạy được, nhưng admin không biết mình đang sửa gì
- Sidebar liệt kê **43 khoá máy** (`questions.guidance`, `category.BE.seniority.Middle.knowledge`, `seniority.Senior.scoring_focus`…) — không nhãn người, không mô tả (`AdminPromptsPage.tsx:32`).
- **Bản mặc định không hiện ở đâu**: `body=null` → textarea trống + placeholder "hệ thống đang dùng prompt mặc định" (`PromptEditorPanel.tsx:15,22`). Mặc định CỐ Ý chỉ nằm trong `prompts.py`/`seniority.py` (`DTOs/PromptTemplate.cs:6-8`). ⇒ admin muốn sửa "Senior nghĩa là gì" phải đọc source Python.
- Không nói **khe này là THAY hay THÊM**, chèn ở đâu, ảnh hưởng luồng nào (sinh câu hỏi? chấm? roadmap?), rủi ro gì. Hai loại khe khác hẳn nhau về hậu quả (`PromptTemplateKeys.cs:20-41`): khe CHẤM sai → điểm sai; khe SINH sai → câu hỏi dở nhưng không mất credit.
- **5/14 khoá cố định là KHOÁ CHẾT** — `criteria.guidance` · `roadmap.guidance` · `lesson_theory.guidance` · `summarize_session.guidance` · `decide_next.guidance`: khai ở .NET, **không builder Python nào đọc** (grep `prompts.py` = 0 hit; test `test_prompt_registry_f21.py:146-150` ghi rõ "5 key chết cũ"). UI vẫn cho sửa → "Đã tuỳ chỉnh" → **không đổi gì** — đúng lớp lỗi mà chính doc comment `PromptTemplateKeys.cs:8-10` cảnh báo.
- Lịch sử: `updatedBy` là **Guid thô** hiện thay tên người (`PromptHistoryList.tsx:8`); history query `enabled: Boolean(selectedKey)` nhưng `selectedKey` khởi tạo `''` → khoá đầu tiên tự chọn **không tải lịch sử** (`useAdminPrompts.ts:11`, `AdminPromptsPage.tsx:19-23`).
- Không có "xem prompt hoàn chỉnh sau khi ghép" / không có chấm thử ngay từ màn này ⇒ sửa xong không biết kết quả.

### C. Nhãn/ngôn ngữ (translations.ts)
- Nav "Duyệt rubric" / title "Duyệt mức rubric" — admin **soạn**, không **duyệt**; "Cấu hình prompt" — "prompt" là jargon dev.
- Mô tả cứng "7 tiêu chí × 6 mức điểm" (`:251`) — sai với CAMP-17 (2–10 mốc).
- Thanh toán: "Chỉ ownerType=0 (Organization)…", "periodUsage", "paymentMode và creditLimit", "Backend vẫn là nguồn quyết định cuối cùng" (`:15-17`) — tên trường API lộ thẳng ra UI; admin **gõ Organization ID bằng tay** (`:20-21`), không có ô chọn.
- Mô tả hộp thoại lưu rubric: "PUT sẽ hạ cờ bộ đang hiệu lực…" (`:261`).

### D. `/admin/roadmap-thresholds` — màn TỐT NHẤT, nên lấy làm mẫu
Hiện đủ 3 giá trị (đang hiệu lực · mặc định · trạng thái Đã chỉnh/Đang dùng mặc định), cảnh báo không hồi tố, validate tại chỗ, phân biệt 400/404, hàng mồ côi, hint "đang sửa N cấp". Còn vướng: mô tả nói *"bỏ trống thì dùng mặc định"* (`translations.ts:280`) nhưng ô trống bị coi **không hợp lệ** (`RoadmapThresholdsTable.tsx:10-11`) — phải bấm "Trả về mặc định"; reset **không confirm**; tên cấp in mã thô, `updatedAt` in ISO thô, `updatedBy` email/id thô; thiếu key `saveError.404`; không nói "đạt" áp cho cái gì.

### E. `/admin/billing` — "Thanh toán tổ chức" thực chất là 2 form mù
- **Không GET gì** — admin không thấy ví đang Prepaid/Postpaid, hạn mức, credit treo; form mặc định luôn `postpaid` (page:17). Service có `getCreditAccount`/`getCreditTransactions` (service:42-43) **không ai gọi**.
- **Kết quả hiện `JSON.stringify` trong `<pre>`** (page:137-141) — admin đọc `ownerType`, `paymentMode: 1`.
- Nhập Organization ID tay ở cả 2 form; màn Tổ chức có ID nhưng **không link/prefill**.
- **Không confirm** 2 hành động tiền; form không reset sau thành công (bấm lần 2 = gửi lại; chốt kỳ có thể ra hoá đơn 0đ). Một `result` dùng chung 2 form → đè nhau.
- "Hạn mức credit" placeholder cứng `"100"` không đơn vị; checkbox "credit đang treo" không giải thích hậu quả.
- Service ném `Error('PAYMENT_MODE_ORG_ONLY'…)` (không phải AxiosError) → `getApiErrorMessage` rơi fallback *"kiểm tra phản hồi từ backend"* — mã lỗi bị nuốt.
- **Cả mảng Payment admin còn lại có service nhưng KHÔNG UI:** orders · packages CRUD · plans CRUD · refund/settle/payout · grant credits · grant subscription · AI usage · traffic (service:11-49, grep 0 caller).

### F. `/admin/dashboard`
- 2 `<select>` không nhãn nhìn thấy cạnh nhau (label `sr-only`) — một cho người dùng, một cho doanh thu; "Làm mới" chỉ refetch analytics người dùng.
- Fetch về nhưng **không render**: gross/paidOrderCount/refunded/aiCost/payingOwnerCount/**toàn bộ funnel**; snapshot chỉ hiện `totalVnd` (không tách quá hạn).
- Trục X biểu đồ người dùng **sai khi nhóm theo tháng** (`AdminAnalyticsChart.tsx:24-27` luôn format ngày). "Chú giải" là 3 span text không màu.
- Jargon không giải thích: "F27", ARPU, MRR; StatCard badge "Tốt" vô nghĩa (mọi metric `healthy`).
- Dashboard hứa "chiến dịch" nhưng `campaign/admin/analytics` + `interview/admin/analytics` có service, **không màn nào gọi**.

### G. Users / Orgs / Campaigns (read + 3 action)
- Users: lọc vai trò hardcode **thiếu `NoRole`** (`AdminDirectoryToolbar.tsx:52`); admin tự gõ mật khẩu mới; map 409/400 → câu phỏng đoán; "Làm mới" ngầm commit ô tìm kiếm.
- Orgs: read-only 4 cột, ID thô để copy, **không link sang Billing/Campaigns** dù 2 màn kia đòi nhập orgId tay; không hiện chế độ ví.
- Campaigns: search **chỉ trong trang hiện tại**; nhập orgId tay (placeholder lộ `orgId`); parser **bỏ hẳn** item có status lạ (`adminCampaignsApi.ts:49-57`); hook không tắt retry 403; page size khác 2 màn kia.
- Layout: không hiện danh tính admin; `AdminPageShell` prop `eyebrow` không ai truyền; comment "17 trang" stale.

### H. Lưới test hiện có
Có test render cho Prompts/Rubrics/Thresholds (nhưng Rubric test khoá **shape sai**); **không có test render** cho Billing, Dashboard, Users/Orgs/Campaigns page, dialogs, layout. Type "mù" `Record<string, unknown>` cho CreditAccount/Transaction/AiUsage/Traffic; enum Payment toàn số không bảng tra.

### I. Bề mặt BE admin đã có vs FE lộ ra — khoảng trống lớn nhất
BE có **~50 endpoint admin** (Auth 7 · Interview 22 · Campaign 2 · Payment 23). FE dùng **~15**. Phần "có BE, FE = 0" — đúng những việc admin đã phải làm bằng curl/SQL trong 2 tháng qua (progress.md):

| Nhóm BE (route gốc) | Endpoint | FE hiện có | Bằng chứng admin đã cần |
|---|---|---|---|
| **Kho tri thức RAG** `interview/admin/knowledge` (list · POST Url/Manual · DELETE · reindex · Context7 search/ingest) | 6 | **0** — service+type có, không màn | 08-08 nạp 25 nguồn, 09-15 lên 45 nguồn, đều qua API |
| **Rubric bộ chuẩn** `interview/admin/rubrics` (matrix · get · PUT · reset · history · suggest · preview · preview-history) | 8 | 1 màn **hỏng** | 09-14 chép 6 bộ × 36 mốc lên prod bằng PUT tay |
| **Prompt** `interview/admin/prompts` | 4 | 1 màn khó dùng | 08-08 đặt `questions.guidance` v1 bằng API |
| **Ngưỡng lộ trình** | 3 | 1 màn tốt | — |
| **Ví/Postpaid** `payment/admin/credits/*` + `invoices/postpaid-overview` + `invoices/close` | 5 | 2 form mù, **không dùng** GET ví / worklist | 07-23 BK24 duyệt postpaid qua API |
| **Đơn hàng & hoàn tiền** `payment/admin/orders` + refund/payout/settle | 4 | **0** | 08-03 hoàn tiền thật 2.000₫ qua API |
| **Cấp credit / thuê bao** `credits/grant` · `subscriptions/grant` | 2 | **0** | 08-07 nạp 5 credit ví test qua API |
| **Plan & Gói** `admin/plans` CRUD · `package` CRUD | 8 | **0** | 09-13 "0 gói active ⇒ không mua được — seed gói" |
| **Báo cáo** auth/interview/campaign analytics · revenue · ai-usage · finance-snapshot · traffic | 7 | 3, render một phần | `ai-usage` (F22) không màn nào — không ai thấy đang đốt bao nhiêu |
| **Chính sách chấm SCP1** | Employer-only; 5 mẫu seed cứng | — | admin không sửa được mẫu qua API (thiết kế) |

**Công tắc chỉ đổi được bằng env (không endpoint, không UI):** Interview `Adaptive:*` · `Scoring:SelfConsistencyN/VarianceThreshold/MinReasoningLen/B2CInactivityMinutes` · `Grounding:*` · `Interview:Topics:Enabled` · `Tiering:Enabled` · `Billing:CvAnalysisCredits/RepoAnalysisCredits` · `DeliveryScoring:*` · `Benchmark:*` — Campaign `Tiering` · `MonitoringGap:*` · `FaceImageRetention:*` · `Invitation:DefaultExpiryDays` · `ApiKeys:*` — Payment `Billing:FreeTrialCredits/UnitPrice/InvoiceDueDays` · `Tiering` · `RefundPayout:*` · `InvoiceOverdue`/`BillingClose`/`OrderExpiry` · `AiPricing` · `Finance:UsdToVndRate` — AIService `TRANSCRIBE_PROVIDER` · `SILENCE_GATE_ENABLED` · `MULTI_VOICE_ENABLED` · `CV_SCREENING_ENABLED` · `DELIVERY_METRICS_SOURCE` · `*_THINKING_BUDGET` · `WHISPER_MODEL`… (đầy đủ file:line trong báo cáo agent). Hệ quả đo được: 3 sự cố "cờ tắt câm" (F21/F22 vắng env nhiều ngày · OPS9 bật Interview quên Campaign · Development mode 6 lần) — **không ai nhìn thấy trạng thái cờ ở đâu ngoài `docker inspect`**.

**Doc lệch:** `interview/admin/roadmap-thresholds`, `interview/admin/analytics`, `payment/admin/traffic` không có trong `docs/services/*.md`; bản copy `src/services/Isas.InterviewService/AGENTS.md` cũng thiếu.

---

## Nguyên nhân gốc (không phải "UI xấu")

1. **FE viết theo hợp đồng tưởng tượng, không đọc DTO BE** (Rubric). Test FE khoá đúng shape sai ⇒ xanh vì lý do sai. L3 chỉ kiểm "trang tải không 400" nên không lộ.
2. **UI lộ ruột máy thay vì khái niệm của admin**: khoá `category.BE.seniority.Middle.knowledge`, JSON thô, `ownerType=0`, GUID, mã enum số. Admin không phải dev.
3. **Màn cấu hình không trả lời 3 câu hỏi bắt buộc**: *(a) cái này là gì, ảnh hưởng luồng nào? (b) giá trị hiện tại · mặc định · đã chỉnh chưa? (c) sửa xong thì thấy gì / hồi tố không?* — Ngưỡng lộ trình trả lời cả 3; Prompt/Rubric/Billing không trả lời câu nào.
4. **Nửa dây**: fetch về không render (matrix, history, sampleQuestions, funnel, snapshot), mutation không nút (reset rubric), kết quả không hiện (chấm thử).
5. **Khai mà không nối**: 5 khoá prompt chết vẫn cho sửa — đúng lớp lỗi "có tên mà không có ruột" (audit FR 07-19).
6. **Không có nơi nhìn trạng thái hệ thống**: mọi công tắc nghiệp vụ là env ⇒ admin không biết vì sao RAG không cite, vì sao buổi không adaptive.

---

## Admin cần gì — 4 việc, 3 câu hỏi

**4 công việc của PlatformAdmin** (suy từ endpoint + việc thật đã làm 2 tháng): ① **Vận hành người dùng/tổ chức** (đã có, ổn) · ② **Tiền** (duyệt postpaid, chốt kỳ, hoàn tiền, cấp credit, gói/plan, doanh thu, chi phí AI) · ③ **Chất lượng AI** (thước đo chấm · hướng dẫn AI · kho tri thức · ngưỡng lộ trình) · ④ **Quan sát hệ** (cờ đang bật gì, báo cáo).

**Nguyên tắc cho MỌI màn cấu hình** (lấy `/admin/roadmap-thresholds` làm mẫu): mỗi mục phải hiện đủ **Đang hiệu lực · Mặc định của hệ · Đã chỉnh bởi ai/khi nào**, có **mô tả một câu "ảnh hưởng luồng nào"**, có **Trả về mặc định** (confirm), có **hồi tố hay không** nói thẳng, và nếu sửa đổi thước đo thì có **chấm thử/xem trước** ngay tại chỗ.

---

## Kiến trúc thông tin đề xuất (nav 4 nhóm thay 8 mục phẳng)

| Nhóm | Mục | Endpoint đã có | Trạng thái |
|---|---|---|---|
| **Vận hành** | Tổng quan · Người dùng · Tổ chức · Chiến dịch | Auth/Campaign admin | có, vá nhỏ |
| **Tiền** | Ví & Postpaid (worklist `postpaid-overview` → chọn org → xem ví/sổ cái → duyệt mode/chốt kỳ) · Đơn hàng & hoàn tiền · Cấp credit/thuê bao · Gói & Tier · Doanh thu & Chi phí AI | Payment 23 | 2 form mù → làm lại |
| **Chất lượng AI** | **Thước đo chấm điểm** (rubric) · **Hướng dẫn AI** (prompt) · **Kho tri thức** (RAG) · **Ngưỡng lộ trình** | Interview 21 | 1 hỏng · 1 khó · 0 · 1 tốt |
| **Hệ thống** | **Công tắc & trạng thái** (mới) | cần BE (xem dưới) | 0 |

Đổi tên: "Duyệt rubric" → **Thước đo chấm điểm**; "Cấu hình prompt" → **Hướng dẫn cho AI**; "Thanh toán tổ chức" → **Ví & Postpaid**.

---

## Thiết kế từng màn (ưu tiên nhóm Chất lượng AI + Hệ thống)

### 1. Thước đo chấm điểm (`/admin/rubrics`) — làm lại theo đúng DTO BE
- **Đầu màn: ma trận 3 nghề × 2 ngôn ngữ** từ `GET /admin/rubrics` (`withLevelsCount/criteriaCount`, version) — chip đỏ khi thiếu mốc; bấm chip chuyển bộ. Trả lời "còn thiếu ở đâu" (BE viết ra chính để làm việc này, `AdminRubric.cs:70-76`).
- **Bảng tiêu chí**: hàng = tiêu chí (tên · scope Always/WhenTargeted · weight · maxScore **khoá, có tooltip vì sao** — `AdminRubric.cs:5-20`) + mô tả sửa được; cột = **mốc THẬT** (không cứng 0..5), nút thêm/xoá mốc, luật CAMP-17 kiểm tại chỗ. **Tái dùng** của wizard employer (đã xác minh tồn tại, `features/employer-campaigns/components/wizard/criteria/`): `CriterionLevelsEditor` · `CriterionLevelRow` · `SuggestLevelsButton` + utils `criterionLevelRules.ts` (`summarizeSuggestedLevels`). ⚠ Dùng chéo từ `features/admin` là vượt ranh giới module ⇒ **nâng lên `src/shared/rubric/`** (tiền lệ `numberQuestions` → `src/shared/utils`, 09-12).
- **AI đề xuất mốc**: `POST levels/suggest` → ghép theo `criterionId`, chế độ điền-ô-trống/thay-tất-cả, seniority = select 4 mức.
- **Chấm thử**: chọn câu mẫu từ `sampleQuestions` (BE trả) hoặc tự gõ; ô "bài tự dán" (textarea); hiện đủ `AdminRubricPreviewRunResponse`: 3 bài Yếu/Khá/Xuất sắc kỳ vọng vs thật, bảng tiêu chí, `freeRunsRemaining`, fingerprint để so trước/sau; **lịch sử 20 lượt** (`GET preview`). Tái dùng `RubricPreviewResult`/`RubricPreviewResultTable`/`RubricPreviewVerdictBlock`/`RubricPreviewHistory` + `rubricPreviewVerdict.ts` (`computeCompression`) của employer (`wizard/preview/`) **qua adapter** — DTO admin (`AdminRubricPreviewRunResponse`) khác DTO employer (`RubricPreviewRun`, có `scopedCriterionIds`).
- **Lịch sử phiên bản + Trả về bản gốc** (confirm, nói rõ: "tạo bản mới, buổi đang luyện giữ bản cũ").
- Hộp thoại lưu viết lại: bỏ "PUT/hạ cờ/ID".
- **Types FE = DTO BE** (`jobCategory`, `criteria[].id`, `levels[].descriptor`, `sampleQuestions`); test fixture lấy từ JSON BE thật (test hợp đồng chéo như E11c).

### 2. Hướng dẫn cho AI (`/admin/prompts`)
- **Nhóm theo luồng** (Sinh câu hỏi · Chấm điểm · Phân tích CV/JD · Cấp độ ứng viên · Theo nghề), mỗi mục: **nhãn người** + 1 câu mô tả + badge **THAY/THÊM** + badge **"Ảnh hưởng điểm số"** (2 khe chấm) + badge Đã tuỳ chỉnh/Mặc định.
- **Editor 2 cột**: trái *Bản mặc định (chỉ đọc, có biến `{role}`)* — phải *Bản của bạn*; nút "Chép mặc định sang để sửa"; ghi chú lý do bắt buộc; đếm ký tự /8000; câu "Có hiệu lực trong ≤60 giây, không cần deploy".
- Khe **THÊM** mặc định trống → hiện "(trống — hệ không thêm gì)" + chỉ vị trí chèn ("sau mọi luật bắt buộc").
- Lịch sử: người sửa (email) + thời gian định dạng; sửa bug khoá đầu không tải lịch sử.
- **Không hiện 5 khoá chết** (gỡ ở BE, xem dưới).
- Nút "Chấm thử để xem ảnh hưởng" ở 2 khe chấm → nhảy sang màn Thước đo (preview đi qua đúng prompt thật).

**Wireframe (chữ) — trước/sau, trả lời câu "có load prompt hiện tại không?" → CÓ:**
```
HIỆN TẠI                                    | ĐỀ XUẤT
[Khoá prompt]        | questions.guidance   | [Sinh câu hỏi]              | Hướng dẫn thêm khi sinh câu hỏi
 seniority.Senior.…  | Phiên bản 0          |  Câu mở đầu vai người hỏi   |  [Thêm vào cuối] [Không ảnh hưởng điểm]
 questions.guidance  | ┌──────────────────┐ |  Hướng dẫn thêm … (đã sửa) |  "Chèn vào lời nhắc sinh câu hỏi B2C/B2B,
 category.BE.…       | │  (ô trống)       │ | [Chấm điểm]                 |   sau dòng số lượng câu. Hiệu lực ≤60s."
 roadmap.guidance ✗  | │ "đang dùng prompt│ |  Vai giám khảo              | ┌─ Mặc định của hệ 🔒 ─┐ ┌─ Bản đang chạy v1 ─┐
 … 38 khoá nữa       | │  mặc định"       │ | [Cấp độ ứng viên]           | │ (trống — không thêm) │ │ Mỗi câu ≤20 từ…    │
                     | └──────────────────┘ |  Senior nghĩa là gì         | └──────────────────────┘ └────────────────────┘
                     | [Ghi chú] [Lưu]      | [Theo nghề] Backend · …     | [Vì sao đổi? *] [Về mặc định] [Lưu bản mới]
                     |                      |                             | Lịch sử: v1 admin@ 08-08 "ép câu ngắn" · v0 mặc định
```
Cột trái "Mặc định của hệ" lấy từ AIService qua B2; cột phải là bản đang chạy (chưa ai sửa ⇒ hai cột giống nhau — **vẫn có chữ**, không còn ô trống). Khe kiểu THAY (`questions.intro`, `scoring.persona`, `seniority.*.profile`, `category.*.display_name`, `*.knowledge`) có chữ mặc định thật; khe kiểu THÊM mặc định trống ghi rõ "(trống — hệ không thêm gì)".

### 3. Ngưỡng lộ trình — vá nhỏ
Sửa mô tả cho khớp code (ô trống = không hợp lệ; muốn mặc định bấm "Trả về mặc định") · confirm reset · format ngày/người · nhãn cấp độ · thêm key `saveError.404` · một câu "đạt = ngưỡng % để một cấp độ được tính ĐẠT trong báo cáo lộ trình".

### 4. Kho tri thức (`/admin/knowledge`) — màn mới, BE đủ
Bảng nguồn (tên · nghề · loại · trạng thái · số chunk · uy tín) · thêm nguồn URL/Manual · tìm & nạp Context7 · reindex · xoá (confirm: xoá Qdrant trước). Banner đọc từ panel Hệ thống: *"Grounding đang TẮT — corpus chưa được dùng"*.

### 5. Ví & Postpaid — làm lại từ worklist
`GET admin/invoices/postpaid-overview` làm bảng chính → chọn org (không gõ GUID) → panel ví (`GET credits/{ownerType}/{ownerId}` + sổ cái) → hành động duyệt mode/chốt kỳ có confirm nêu hậu quả, kết quả hiện có cấu trúc (bỏ `<pre>`), form reset sau thành công. Từ màn Tổ chức có link "Xem ví".

### 6. Công tắc & trạng thái hệ thống (`/admin/system`) — MỚI, trả lời câu hỏi của user
**Câu hỏi:** *"cho admin công tắc bật/tắt Adaptive · Tiering · Grounding · SelfConsistencyN · MonitoringGap · SILENCE_GATE được không?"* — **Được, nhưng chia 2 tầng theo 3 tiêu chí**: (i) một service sở hữu hay xuyên service? (ii) sai chiều nào có mất tiền/lộ dữ liệu không? (iii) hiệu lực có ghim được theo buổi/lượt mới không?

| Cờ | Tầng | Lý do |
|---|---|---|
| `Adaptive:Enabled` (sàn INT-19), `Adaptive:MaxDeepPerQuestion` (kill-switch chuỗi) | **Bật/tắt runtime** | 1 service; buổi ghim `adaptive_enabled` lúc tạo; tắt không mất tiền — nhưng UI cảnh báo "adaptive là engine mọi tier, tắt = mọi người trả credit mất tính năng" |
| `Grounding:Enabled`, `Grounding:TopK/ScoreThreshold` | **Runtime** | 1 service; degrade về ungrounded an toàn (D27); panel nói rõ cần Qdrant + corpus |
| `Interview:Topics:Enabled`, `Roadmap:LessonAdaptiveEnabled` | **Runtime** | kill-switch thuần, 1 service |
| `Scoring:SelfConsistencyN` | **Runtime có rào** | 1 service nhưng là **cần gạt tiền** (×N lượt Gemini/answer, đo $0,0154/lượt) → dialog hiện ước tính chi phí; **ghim N vào `practice_sessions` lúc tạo** (tránh đổi giữa buổi làm answer chờ mãi/đủ sớm) |
| `MonitoringGap:Enabled` (+`GapThresholdSeconds`) | **Runtime có rào** | Campaign sở hữu; đang shadow-mode và **chưa hiệu chuẩn** (B5: resume >24h bị coi là gap) → nút bật kèm cảnh báo đỏ, mặc định TẮT |
| `SILENCE_GATE_ENABLED`, `MULTI_VOICE_ENABLED`, `DELIVERY_METRICS_SOURCE`, `CV_SCREENING_ENABLED` | **Runtime qua registry** | AIService không có DB (GEN-4) → **kéo từ Interview `/internal/settings` y hệt cách kéo prompt** (TTL 60s, fail-open về env). `MULTI_VOICE` kèm cảnh báo "chưa hiệu chuẩn giọng thật" |
| `TRANSCRIBE_PROVIDER` | **Chỉ đọc** (đợt này) | đổi = giọng ứng viên rời hạ tầng (DATA-3), là quyết định sản phẩm + điều khoản, không phải nút |
| `Tiering:Enabled` | **Chỉ đọc** | **3 service** phải lật cùng lúc — công tắc runtime không atomic tái tạo đúng OPS9; và bật = đổi cách tính tiền (quota metered). Đổi bằng env qua PR |
| `Billing:FreeTrialCredits/UnitPrice`, `RefundPayout:Enabled/MaxAutoPayoutVnd`, `InvoiceOverdue/BillingClose/OrderExpiry`, `AiPricing`, `Finance:UsdToVndRate` | **Chỉ đọc** | tiền thật đi ra/vào — cần change control (PR review), không phải một click |
| `WHISPER_MODEL`, `GEMINI_MODEL`, thinking budgets, prefetch, thread pool | **Chỉ đọc** | tham số hạ tầng, đổi cần đo lại (đã có 4 vòng A/B) |

**Cơ chế — tái dùng 2 mẫu đã có, không phát minh:**
- **Lưu**: bảng `platform_settings` trong **Interview** (mirror `prompt_templates`: `key · value · version · is_active · updated_by · updated_by_email · change_note · created_at`, append-only, DELETE = về mặc định giữ lịch sử) + `GET/PUT/DELETE /api/admin/settings` với **danh sách khoá ĐÓNG** (`PlatformSettingKeys`, mẫu `PromptTemplateKeys`: khoá lạ → 400). Campaign có bảng tương tự cho cờ của mình (`MonitoringGap`). **Không** làm một service "settings trung tâm" cho cờ xuyên service — đó chính là lý do `Tiering` ở tầng chỉ-đọc.
- **Đọc**: `EffectiveSettings` overlay **DB override > env/appsettings** — đúng mẫu `RoadmapThresholdService` (`EffectivePct/DefaultPct/IsOverridden`) đang chạy; consumer hiện tại (`AdaptiveOptions`, `GroundingOptions`, `ScoringOptions`) đổi sang đọc qua overlay ở **điểm tạo buổi/lượt** (không đổi giữa buổi). AIService: mở rộng `prompt_registry.py` thành pull `/internal/settings` cùng nhịp (TTL, fail-open 4 tầng).
- **Panel FE**: 1 màn, nhóm theo service; mỗi dòng: nhãn · mô tả 1 câu "ảnh hưởng gì" · **Đang hiệu lực** · **Mặc định (env/appsettings)** · nguồn (`admin` / `env` / `mặc định`) · ai sửa/khi nào · nút Bật/Tắt (tầng runtime, có confirm nêu hậu quả) hoặc badge **"Đổi bằng env → cần deploy"** (tầng chỉ đọc). Kèm **revision image** mỗi service (label `org.opencontainers.image.revision` qua env `IMAGE_REVISION`) — khép OPS1 luôn.
- **Endpoint đọc trạng thái** (cho tầng chỉ-đọc + để panel biết mặc định): mỗi service `GET /admin/config-status` trả allowlist khoá an toàn (**không secret**), Roles=Admin; AIService `GET /internal/config-status` do Interview proxy (GEN-7).
- **Luật an toàn bắt buộc** cho tầng runtime: hiệu lực chỉ với buổi/lượt MỚI + ghim vào buổi · mọi lần đổi có version+actor+lý do · confirm nêu hậu quả (+ chi phí nếu là cần gạt tiền) · "Trả về mặc định" luôn có · **mutation-check từng consumer**: gỡ overlay → test đỏ; tắt registry → hành vi = env (fail-open).

---

## Thay đổi BE cần cho đề xuất (nhỏ, cùng mẫu sẵn có)

| # | Service | Việc | Vì sao |
|---|---|---|---|
| B1 | Interview | `PromptTemplateResponse += Label, Description, Kind(Replace/Append), UsedIn[], Risk, Group, DefaultBody?` — catalog tĩnh cạnh `PromptTemplateKeys` (doc-comment sẵn có chuyển thành dữ liệu) | FE không thể tự biết khe làm gì; đây là chỗ duy nhất biết |
| B2 | AIService | `GET /internal/prompts/defaults` **stateless** (X-Internal-Token) trả `{key: template mặc định}`; gom literal mặc định trong `prompts.py`/`seniority.py` về một dict + test khoá "builder dùng đúng dict đó" (giữ bất biến byte-identical) | Bản mặc định chỉ tồn tại ở Python (GEN-4, `DTOs/PromptTemplate.cs:6-8`); Interview ghép `DefaultBody`, cache, fail-open → `null` + UI báo |
| B3 | Interview + AIService | **Gỡ 5 khoá chết** khỏi `PromptTemplateKeys.All`; co `dead_keys` trong `test_prompt_registry_f21.py` (test cho phép "chỉ được co lại") | UI trung thực. Muốn nối thật (roadmap/lesson/summarize/decide-next/criteria) là task riêng có xét AI-4 |
| B4 | Interview | `prompt_templates.updated_by_email` snapshot claim email lúc ghi (mẫu `ranking_overrides.actor_email` E11c) | GEN-3: không tra Auth lúc chạy; Guid thô vô nghĩa với admin |
| B5 | Interview | `platform_settings` + `PlatformSettingKeys` đóng + `GET/PUT/DELETE /api/admin/settings` + overlay `EffectiveSettings` + ghim `self_consistency_n`/`adaptive_enabled` (đã có) vào buổi | Tầng công tắc runtime |
| B6 | Interview | `GET /internal/settings` cho AIService kéo; AIService mở rộng `prompt_registry` → `registry.py` (prompts + settings cùng TTL) | Cờ AIService bật/tắt không deploy |
| B7 | Campaign | `platform_settings` riêng (MonitoringGap) + admin endpoint cùng mẫu | Service sở hữu dữ liệu giữ endpoint admin (AUTH-7) |
| B8 | 4 service + AIService | `GET /admin/config-status` (AIService qua Interview proxy) allowlist không secret + `IMAGE_REVISION` | Tầng chỉ-đọc + mặc định cho panel |
| B9 | Docs | `interview.md` thêm mục roadmap-thresholds/analytics/settings; `payment.md` thêm `admin/traffic`; đồng bộ bản copy `AGENTS.md` **có nhắm** (không `cp` đè) | Doc lệch code |
| B10 | 4 service | Gắn `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]` lên **request DTO admin** (`UpsertAdminRubricRequest`, `AdminRubricPreviewRequest`, `UpdatePromptTemplateRequest`, `SetPaymentModeRequest`, `GrantCreditRequest`, …) — .NET 8+ (**Context7 `/dotnet/docs` xác nhận**: JSON có trường lạ → `JsonException` → 400) | Bịt tận gốc lớp lỗi "FE gửi sai tên trường, BE 200 và âm thầm no-op" — chính lớp lỗi làm Rubric hỏng và đã cắn repo 5 lần (`focusCriteria` · `metricsVersion` · `adaptiveMaxQuestions` · `Seniority` · rubric). Chỉ áp DTO admin (nội bộ, không có client cũ ngoài FE) |

Migration mới: Interview 2 (`updated_by_email`, `platform_settings`), Campaign 1 — đều additive; nhớ luật "migration đi TRƯỚC/CÙNG deploy" (sự cố `42703` ×4).

**Đã kiểm qua Context7 (2026-09-16):**
- `/dotnet/aspnetcore.docs` — có mẫu chính thức `EFConfigurationProvider` (custom `IConfigurationProvider` đọc DB). **Cố ý KHÔNG dùng** cho B5: chạy trước DI (tự mở DbContext riêng), `Load()` một lần trừ khi tự `OnReload()`, bản mẫu gọi `EnsureCreated()` (trái no-auto-migrate), đổ mọi khoá vào `IConfiguration` nên **mất "nguồn"** (admin/env/mặc định) mà panel cần hiện và không có danh sách khoá đóng. Overlay `EffectiveSettings` (mẫu `RoadmapThresholdService` đang chạy) giữ được cả ba.
- `/dotnet/docs` — `JsonUnmappedMemberHandling.Disallow` có từ .NET 8 (attribute trên type hoặc `JsonSerializerOptions.UnmappedMemberHandling`) → B10.
- Không có câu hỏi thư viện nào khác: prompt/settings pull phía Python dùng lại `prompt_registry.py` (module cache, không đụng pydantic-settings); FE fix `history` là logic state, không phải API TanStack.

**Đã tự xác minh lại các khẳng định của agent** (không tin báo cáo): Billing `<pre>{JSON.stringify}` `AdminBillingPage.tsx:138-139` ✓ · Billing 0 `useQuery` ✓ · `AdminAnalyticsChart.tsx:24-27` format ngày bất kể nhóm tháng ✓ · `isValidThresholdPct` chuỗi rỗng = false ✓ · `AdminRubricsPage.tsx` đọc `preview.data` **0 lần** ✓ · 8 component/utils employer để tái dùng **tồn tại** (tên đã sửa đúng ở mục 1).

---

## Phân đợt đề xuất (để duyệt riêng từng đợt — KHÔNG làm trong đợt này)

| Đợt | Nội dung | Kho | Ước lượng |
|---|---|---|---|
| **A — chữa cháy (FE-only)** | Rubric: sửa hợp đồng theo DTO thật, cột mốc động, render kết quả chấm thử + ma trận + lịch sử + reset; Prompt: ẩn 5 khoá chết (danh sách tạm ở FE), fix lịch sử khoá đầu; Billing: bỏ JSON thô + confirm + GET ví; Thresholds: copy/confirm; purge jargon translations | FE 1 PR | ~1–1,5 ngày |
| **B — hiểu được (BE nhỏ + FE)** | B1–B4 + Prompt 2 cột mặc định/bản sửa + nhóm theo luồng + nav 4 nhóm + đổi tên mục | BE 1 PR · FE 1 PR | ~1,5–2 ngày |
| **C — công tắc & trạng thái** | B5–B8 + màn `/admin/system` (tầng runtime + tầng chỉ đọc) | BE 1 PR (Interview+Campaign+Payment+Auth+AIService) · FE 1 PR | ~2–3 ngày |
| **D — mở bề mặt BE sẵn có** | Kho tri thức · Ví & Postpaid từ worklist · Đơn hàng/hoàn tiền · Cấp credit/thuê bao · Gói & Tier · Chi phí AI; org picker dùng chung | FE nhiều PR, chia theo sở hữu file | ~4–6 ngày |

Đợt A đứng độc lập và **đáng làm ngay** vì đang có một màn lưu-không-lưu trên prod.

---

## Xác minh (khi từng đợt được duyệt làm)

- **A/Rubric**: trên dev, mở `/admin/rubrics` → bảng **hiện descriptor thật** của bộ BE/vi (prod có 36 mốc) → sửa 1 ô → Lưu → `GET /api/v1/interview/admin/rubrics/BE` version +1 và `changed:true`; ma trận 6 ô hiện `withLevelsCount`; chấm thử → 3 bài hiện điểm, `freeRunsRemaining` giảm 1; test FE fixture = JSON BE thật; mutation: đổi `descriptor`→`description` trong mapper → test đỏ.
- **A/Prompt**: 5 khoá chết không còn trong sidebar; chọn khoá đầu → lịch sử tải (network có `/history`).
- **B**: `GET /api/v1/interview/admin/prompts` trả `label/description/kind/defaultBody`; tắt AIService → `defaultBody:null` + UI báo, màn vẫn dùng được (fail-open); pytest `test_khoa_python_va_dotnet…` xanh với `dead_keys` rỗng; mutation: đổi 1 literal mặc định trong builder mà quên dict → test đỏ.
- **C**: bật `Grounding` từ panel → buổi mới có `citations`; buổi đang dở không đổi; tắt registry (env rỗng) → AIService dùng env như cũ; `Tiering` hiện badge "cần deploy" và **không có nút**; mutation: gỡ overlay ở `PracticeService` → test đỏ; panel 5 service hiện `IMAGE_REVISION` khớp `docker inspect`.
- **Mọi đợt**: `npm test -- --watch=false` (FE) · `dotnet test` · pytest · `has-pending-model-changes` · L3 trên dev bằng Chrome thật (in-app browser treo với app này), chụp ảnh **trước/sau** — không nhận "HTTP 200" làm bằng chứng (bài học rubric).

## "Đổi có hiệu lực liền không?" — trả lời cho từng loại (đã hỏi 2026-09-16)

| Cái admin sửa | Hiệu lực | Buổi đang dở |
|---|---|---|
| Prompt | Lượt gọi Gemini kế tiếp, trễ ≤60s (`prompt_cache_ttl_seconds`, `config.py:412`); không deploy. Đã chạy thật prod 08-08 (`questions.guidance` v1). | Câu đã sinh giữ nguyên; chấm dùng bản mới cho lượt kế, có dấu `prompt_version`. |
| Rubric mốc | Buổi tạo sau đó — liền. | **Giữ bản cũ tới hết** (BC-8, một buổi = một thước). Hộp thoại Lưu phải nói điều này bằng tiếng người. |
| Ngưỡng lộ trình | Lần tính báo cáo kế. | Lộ trình Completed giữ ngưỡng cũ (không hồi tố). |
| Công tắc runtime (đề xuất) | Interview: lúc tạo buổi/lượt mới — liền. AIService: kéo về ≤60s. | Ghim vào buổi (`adaptive_enabled` đã có; thêm `self_consistency_n`). |
| Tầng chỉ-đọc (Tiering, cờ tiền, model) | **Không** — env + deploy. Cố ý. | — |

⚠ AIService có **2 tiến trình** (`aiapi`, `aiworker`) với cache riêng ⇒ trong ≤60s có thể một bên mới một bên cũ. Panel công tắc nên hiện trạng thái **"đang lan (≤60s)"** thay vì tick xanh ngay, để admin không tưởng hỏng.

## Rủi ro / quyết định còn mở
- Gỡ 5 khoá chết vs nối thật — đề xuất **gỡ** đợt B, nối là task riêng.
- Ưu tiên DB override > env cho tầng runtime: ops đặt env sẽ bị admin override "thắng" — panel phải hiện `nguồn` để không ai đuổi nhầm.
- `SelfConsistencyN` là cần gạt tiền: cần chốt có cho admin đổi hay chỉ per-plan (`Plan.SelfConsistencyN` đã có).
- Tên mục nav mới cần team đồng ý trước khi đổi i18n (16 file parity).


---

## Đợt A đã làm (2026-09-16 chiều) — FE `isas-web-client` nhánh `fix/admin-config-a` off `origin/dev` `e006734`, 9 commit, CHƯA push

| Commit | Việc |
|---|---|
| `45620f3` | **Rubric viết lại theo DTO BE**: types = `AdminRubric.cs`; parser parse-first (lệch hợp đồng ⇒ NÉM); PUT chỉ `{id, description, levels[{score, descriptor}]}`; mốc động qua `CriterionLevelsEditor` employer; ma trận 3×2; chấm thử đúng hợp đồng + render qua `RubricPreviewResult` (adapter); lịch sử phiên bản + lịch sử chấm thử; reset có confirm; AI đề xuất ghép theo `criterionId` |
| `245fb68` | **Prompt**: nhãn người/nhóm theo luồng/loại khe/rủi ro (`adminPromptCatalog.ts`), ẩn 5 khoá chết, badge "Ảnh hưởng điểm số", đếm ký tự/8000, lịch sử khoá đầu tải được, Guid không in thô |
| `72ea3bb` | **Billing**: một tổ chức làm ngữ cảnh (`?orgId=`, link "Xem ví" từ màn Tổ chức), GET ví hiện chế độ/credit, confirm 2 hành động tiền, kết quả có cấu trúc (bỏ `<pre>` JSON), form reset, chốt kỳ khoá khi Prepaid, bảng tra enum SỐ Payment |
| `3dd5275` | **Ngưỡng lộ trình**: mô tả khớp code, reset có confirm, ngày/nhãn định dạng, key `saveError.404` |
| `4eab516` | Dọn jargon copy (token/refresh token/Auth/F27/orgId) |
| `69ed4af` | Bảng thước đo bỏ lớp phủ trắng (theme guard); gỡ `RubricLevelsTable` khỏi danh sách nợ |
| `ebf9010` | Test lọc khoá chết đo bằng SỐ MỤC sidebar (mutation M6 xanh giả với phép đo cũ) |
| `a2a6247` | e2e Playwright `admin/rubrics.spec.ts` (Chromium thật, response mock đúng DTO) + cập nhật copy spec ban/reset |
| `(L3 fix)` | Ma trận gọi KHÔNG kèm `language` (BE trả cả 6 ô); thiếu mốc là cảnh báo mềm — BE chỉ đòi mốc ở tiêu chí AI chấm (`MeasuredCriteriaSplit.ForAi`), DTO admin chưa lộ `scoringMethod` |

**Gate:** `tsc` 0 error · i18n parity 16 · ui-size ≤250 · radius · **vitest 1851/1851** (baseline dev 1796) · `vite build` OK · **Playwright smoke+admin 36/38** — 2 đỏ (`platform.spec` trỏ `/admin/audit-logs` đã cắt 09-13; `auth-analytics`) **đã đỏ y hệt trên `origin/dev`** (verify bằng worktree baseline riêng), không do đợt này.

**Mutation-check 12 phép** (harness so `sha256`, `os.utime`, baseline lại sau mỗi phép): **11 ĐỎ ngay**; **M6 XANH → điều tra**: bỏ lọc khoá chết thì chúng hiện dưới nhãn "chưa nhãn" chứ không hiện khoá thô ⇒ test cũ mù; sửa test đo số mục sidebar ⇒ ĐỔ.

**L3 với backend dev thật (Chrome, FE local `:5175` → gateway dev):**
- Rubric: 6/6 ô ma trận hiện độ phủ thật (6/7 · v3) · bảng hiện **descriptor thật** · sửa mô tả → Lưu → confirm → **PUT 200 → v3→v4**, mô tả persist sau refetch (điều bản cũ **chưa bao giờ** làm được) · **chấm thử thật POST 200** → "Lượt 5 · thước đo v4 · Thứ tự đúng · biên độ 30 điểm" · 3 dải 60/70/90% · "còn 4 lượt miễn phí" · 4 lượt lịch sử cũ (06/09) render qua adapter.
- Prompt: nhóm theo luồng, badge THÊM/THAY + rủi ro, lịch sử khoá đầu tải ngay (`/criterion_levels.guidance/history` GET lúc load).
- Billing: "Xem ví" từ Tổ chức → `?orgId=` → GET ví 200 → Trả trước · 25 còn · 2 đang giữ; chốt kỳ tự khoá kèm lý do. **Không** đổi chế độ ví thật trên dev.
- 0 console error trên cả 3 màn.

**Hai lỗ chỉ L3 mới lộ** (unit test + mutation không thấy): ma trận thiếu 3 ô English (query kèm `language`) · chặn cứng chấm thử sai vì không biết `scoringMethod`. ⇒ **Đợt B phải thêm `scoringMethod` vào `AdminRubricCriterionItem`** để FE nói đúng "tiêu chí này đo bằng số, không cần mốc".

**Dấu vết để lại trên dev (bằng chứng L3):** bộ chuẩn BE/vi **v4** (mô tả "Chiều sâu kỹ thuật" thêm hậu tố "(L3 2026-09-16)") + 1 lượt chấm thử (lượt 5, ~$0,05).

**Còn (không thuộc đợt A):** chưa push/PR · 2 spec e2e stale (`platform.spec` audit-logs/ai-config, `auth-analytics`) nên xoá/sửa ở task riêng · đợt B (BE: metadata + defaultBody + gỡ 5 khoá chết + `updated_by_email` + `scoringMethod` + `JsonUnmappedMemberHandling.Disallow`) · đợt C (công tắc) · đợt D (Knowledge/Plans/Orders/Grant).


---

## Đợt B (một phần) đã làm cùng ngày — hiện BẢN MẶC ĐỊNH prompt (B1-defaults · B2 · B3)

User hỏi *"không thể load prompt hiện tại trong hệ thống lên để họ edit à?"* → làm ngay.

| Kho | Nhánh | Việc |
|---|---|---|
| BE `isas-server` | `feat/admin-prompt-defaults` off `origin/dev` (`ee47f4c`, CHƯA push) | AIService `app/prompt_defaults.py` + `GET /api/v1/prompt-defaults` (X-Internal-Token, stateless — GEN-4 nguyên); Interview `PromptTemplateResponse += DefaultBody?` + `AiServicePromptDefaultsClient` (cache 300s, **fail-open** ⇒ null) + ghép trong `ListAsync`; **gỡ 8 khoá chết** khỏi `PromptTemplateKeys` (5 cũ + `category.<nghề>.description` — cũng không ai đọc) ⇒ còn **35 khoá**; pytest chéo nay đòi danh sách chết RỖNG. Doc `ai.md`/`interview.md`/AGENTS copy. |
| FE `isas-web-client` | `fix/admin-config-a` (thêm commit, CHƯA push) | Editor 2 cột: trái "Bản mặc định của hệ" (chỉ đọc) · phải "Bản của bạn" · nút **Chép mặc định sang để sửa**; 3 trạng thái tách bạch: chuỗi (kèm ghi chú biến `{role}`/`{job_category}`) · `""` = khe THÊM trống (không nút chép) · `null` = "chưa lấy được" (không giả vờ trống). |

**Gate:** Interview **1749** (+7) · pytest **1213** (+4) · solution build 0 error · FE vitest **1853** · build · e2e admin pass. **Mutation 5/5 ĐỌ** (BE: thiếu khoá ⇒ "" · lỗi ⇒ bản đồ rỗng · quên token; FE: nút chép hiện khi mặc định trống · null coi như trống).
**L3:** AIService chạy thật local → `/prompt-defaults` **401** không token · **200** có token · **35 khoá** (= `PromptTemplateKeys.All`); FE trên dev với BE cũ rơi đúng nhánh **null** ("chưa lấy được", vẫn sửa/lưu được); ảnh happy-path chụp bằng cách ghép defaults thật (từ AIService local) vào phản hồi dev.
**Chưa:** deploy BE lên dev (cần `scripts/deploy-dev.sh interview ai`) để thấy happy-path end-to-end; `updated_by_email` (B4) và `JsonUnmappedMemberHandling.Disallow` (B10) chưa làm; `scoringMethod` trên `AdminRubricCriterionItem` chưa thêm.

### Cập nhật cùng ngày — editor prompt về MỘT ô + deploy dev + 1 bug BE lộ ở L3
- User xem ảnh 2 cột rồi chốt: *"quay trở lại thành 1 khung"* + *"chả thấy cái mặc định nào cả"* (dev đang chạy BE cũ nên cột mặc định trống). Đổi thiết kế: **một ô đổ sẵn chữ đang chạy** (`body ?? defaultBody`), sửa thẳng → Lưu; Lưu chỉ bật khi khác bản đang chạy; "Hoàn tác sửa"; khe đã tuỳ chỉnh có `<details>` "Xem bản mặc định của hệ"; sidebar 35 mảnh **dính + tự cuộn** (bấm mảnh cuối trước đây làm ô sửa trôi khỏi màn hình — lộ ở ảnh L3). FE `096930ea`; test 11, mutation 6/6 ĐỎ.
- **Deploy dev** Interview + AIService nhánh `feat/admin-prompt-defaults` (`scripts/deploy-dev.sh interview ai`, 0 migration; revision đang chạy là tổ tiên của nhánh). Dev trả `defaultBody` cho **35/35** khoá (12 khe THÊM = `""`, 0 null).
- 🔴 **Bug BE có sẵn từ F21, chỉ lộ khi chạy trọn luồng:** lưu v1 → "Về mặc định" → lưu lại ⇒ **500** `23505 duplicate key ix_prompt_templates_key_version` — `next` tính trên bản ACTIVE (sau reset = 0) ra 1, trùng v1 trong lịch sử. Vá `635f11b`: tính trên mọi bản của khoá. ⚠ Test lượt đầu **XANH kể cả khi dựng lại bug**: cùng một DbContext nên hàng v1 tracked với `IsActive=true` cũ, EF identity-resolution trả bản tracked thay bản DB; phải `ChangeTracker.Clear()` giữa các lời gọi (mô phỏng mỗi request một context) → mutation ĐỎ. Interview 1750.
- L3 dev thật sau vá: mở → ô có chữ → sửa → lưu **v2** → Về mặc định → khe THÊM trống, 0 lỗi. Dấu vết dev: `seniority.Senior.profile` lịch sử v1+v2 (đều đã hạ cờ, hiện chạy mặc định).
- **Mốc "Độ trôi chảy & tự tin" (user yêu cầu):** tiêu chí này `ScoringMethod=Measured` — `DeliveryFluencyScorer` chấm từ tỉ lệ im lặng theo bảng ngưỡng (`DeliveryScoringOptions.SilenceBands`: ≤0,036→5 · ≤0,144→4 · ≤0,254→3 · ≤0,359→2 · còn lại→1; ≥4 lần dừng dài −1 bậc; `MinBand=1`; nói <10s ⇒ LOẠI khỏi điểm), `level_matched = null` ⇒ **mốc KHÔNG tham gia tính điểm**, chỉ là lời giải nghĩa bậc. Đã thêm 6 mốc (0–5, vi+en) **viết theo đúng ngưỡng đang chạy** vào cả 6 bộ trên dev qua PUT admin (BE/vi v4→v5, 5 bộ còn lại v3→v4; 6 tiêu chí kia giữ nguyên mốc). Ma trận dev nay **7/7 × 6**. ⚠ Mô tả mang con số ngưỡng ⇒ ai đổi `SilenceBands` phải sửa mốc theo — không có guard; đúng hơn là lộ `scoringMethod` trên `AdminRubricCriterionItem` để UI nói "chấm bằng số đo" thay vì đòi mốc (đợt B còn lại). Soft-version **mint id mới** cho mọi tiêu chí ⇒ script ghép theo TÊN, không theo id (lượt đầu vấp).

### Hiệu chuẩn CHẤM THỬ (user: "sao yếu/khá/xuất sắc chênh lệch dữ vậy — tự làm tự test tối ưu, tối ưu lại mốc")
- **Đo trước** (dev + local, 6 lượt, bộ chuẩn BE/vi v5, 3 câu mẫu, Gemini thật): Yếu **60%** (kỳ vọng 20) · Khá 69% (60) · Xuất sắc **81%** (100); dải XS−Yếu **20 điểm**.
- **Chẩn đoán từ lý do chấm** (không đoán): (1) model viết bài "yếu" ĐÚNG-mà-nông rồi chỉ đổi giọng ("dạ… cái này á") — bộ chấm cho 3/5 là chuẩn; câu cũ trong prompt *"chỉ nông và có chỗ sai"* chính là thứ kéo về bài đúng-nông. (2) Bài "xuất sắc" thiếu đúng yếu tố mốc 4–5 (giá phải trả, triệu chứng khi chịu tải). (3) **Mốc chỉ mô tả cái CÓ, không mô tả cái SAI** ⇒ bài trộn đúng/sai được cộng điểm phần đúng, câu sai bị bỏ qua (gọi index là "khoá chính" vẫn 4/5 Thuật ngữ). Bộ chấm phân bậc đúng; lệch nằm ở người viết mẫu + cửa chặn của mốc.
- **5 biến thể, ~40 lượt Gemini (~$2):** A (trần/sàn + tự kiểm) không đổi · B (kế hoạch trước bài) XS +3 · **C (Yếu = HIỂU SAI, ≥2 câu sai thật) Yếu 60→51, XS 81→88** · D (câu sai phải là ý chính, không kèm bản đúng) Yếu 46 · E (temperature 0.6 + "ĐÚNG LÀ") không khá hơn D → giữ 0.9. Mốc v6 (cửa chặn "không có phát biểu sai, còn một câu là rơi về mốc 2" ở mốc ≥3; mốc 1–2 định nghĩa bằng lỗi) cắn ở tiêu chí nội dung (Chiều sâu Yếu 2.0→1.67, Thuật ngữ 2.17→1.67), gộp dải 37→41.
- **Sau, đo qua đúng đường admin trên dev** (AIService `e154e23` + mốc v6, 3 lượt): Yếu **47%** · Khá 70% · Xuất sắc **83%**; dải **37 điểm**; thứ tự đúng 100% (trước lẫn sau, 27/27 lượt local). Dev: BE/vi v5→**v6** (full), 5 bộ còn lại v4→v5 (3 tiêu chí cách nói dùng chung nhận mô tả v6, bản en dịch tương ứng; tiêu chí nội dung nhận thêm mệnh đề cửa chặn).
- **Phần lệch còn lại là CẤU TRÚC, không phải lời dặn:** (a) 2/6 lượt model vẫn viết bài "sai" mà thật ra đúng (idempotent bằng primary key trên transaction_id) — model từ chối viết sai kiến thức có hệ thống; (b) bài sai kiến thức vẫn nói mạch lạc, đúng ngữ pháp ⇒ 3/5 ở Giao tiếp/Ngữ pháp trong khi kỳ vọng Yếu = mốc 1 ở MỌI tiêu chí (`ExpectedLevels.For`: n/4 → 1). Bài viết "yếu" tự nhiên của model là mốc 2 ("đúng bề mặt, không đi sâu"), không phải mốc 1 ("nói sai"). ⚠ Vì thế câu chẩn đoán của FE (`computeCompression`: *"Yếu bị chấm CAO hơn ở 6/6 ⇒ mốc thấp quá dễ đạt"*) đang **đổ lỗi cho mốc** thứ thuộc về người viết mẫu — cần chốt: đổi kỳ vọng Yếu sang mốc 2 (`ExpectedLevels` ở Isas.Shared, ảnh hưởng cả báo cáo employer) hoặc đổi câu chữ verdict.
- ⚠ Tiêu chí "Độ trôi chảy & tự tin" **không** vào chấm thử (đo bằng số đo, `MeasuredCriteriaSplit`) — 6 tiêu chí AI mới là phần được đo.

### Đổi hướng: "TỰ THỬ THƯỚC ĐO" — người dùng NÓI/dán rồi xem hệ chấm mình (user: "không cần 3 bài AI lắm; cho thử nói và test câu họ tạo; hỏi designer + UX/UI")
- 2 agent (UX luồng · UI bố trí) đọc panel/bộ ghi âm/ảnh → chốt: nói là trung tâm, dán là lối thoát; **không có đường bấm Chấm trước khi thấy bản chép lời**; 3 bài AI = checkbox tắt mặc định (bật mới tốn 3 lượt Gemini); số lượt in trên nút; dán tay ⇒ hàng trôi chảy nói rõ "không chấm"; đổi câu hỏi khi đã có bài ⇒ hỏi trước.
- **BE (`637bc30`, `60da84c`, `0da90b9`):** `POST admin/rubrics/{cat}/preview/transcribe` (multipart ≤15 MB → AIService `/transcribe`, không tốn lượt/không lưu, `noSpeech`) · `AdminRubricPreviewRequest += includeAiSamples (default true) + deliveryMetrics` · AIService `/score-preview += includeAiSamples, customDelivery` (số đo CHỈ gắn bài Custom) · bài Custom có số đo ⇒ **hàng đo trôi chảy** qua `DeliveryFluencyScorer` (cùng ngưỡng đường chấm thật; <10s nói ⇒ loại, không 0) · `AdminPreviewSampleScore.measured` · `/transcribe += rejectReason`. Interview 1761 · pytest 1220 · mutation 8/8 + 3/3 ĐỎ. **0 migration.**
- 🔴 **Sự cố tự gây trên dev:** `customDelivery: DeliveryMetrics | None` tham chiếu class khai phía dưới ⇒ Python 3.12 (image) `NameError` lúc import ⇒ **aiapi-dev crash-loop**; venv local là 3.14 (hoãn đánh giá annotation) nên pytest xanh — **test không bắt được, deploy mới lộ**; `deploy-dev.sh` báo ✅ vì chỉ đọc label + gateway 200, không kiểm health container. Vá forward-ref chuỗi (`60da84c`). ⚠ **Venv 3.14 ≠ image 3.12 là rủi ro thường trực** — nên pin venv về 3.12 hoặc để `deploy-dev.sh` kiểm `docker inspect .State.Health` từng container.
- **L3 BE trên dev:** transcribe WAV giọng thật 7s (whisper-1, có số đo) → chấm chỉ bài mình **9,4s** (thay 40–80s), **7/7 tiêu chí** kể cả trôi chảy 4/5 (im lặng 11,5%).
- **FE (nhánh `fix/admin-config-a`):** `useRubricTryFlow` (state machine nói/dán/chép/sửa/chấm) · `RubricTryQuestionRow` · `RubricTryVoiceRecorder` (tái dùng nguyên `useAudioRecorder`, `sessionId='admin-rubric-try'`) · `RubricTryAnswerInput` · `RubricTryYourScore` (tái dùng `SpeechMetricsPanel` qua adapter) · panel điều phối; 3 bài AI vào `<details>`. i18n `admin.rubrics.try.*` vi/en. Test: page 8 · hook 4 · recorder 5 · result 4 · service 2; mutation 7/8 ĐỎ (M1 phòng thủ dư có lý do: `switchMode` luôn xoá số đo). 🔴 **Lỗ chỉ L3 mới lộ:** `apiClient` mặc định `Content-Type: application/json` ⇒ FormData lên BE **415**; curl -F thì 200 ⇒ phải đặt `multipart/form-data` tường minh (test khoá).
- **L3 FE trên dev bằng Chrome giả mic** (`--use-fake-device-for-media-stream --use-file-for-fake-audio-capture=<WAV 48k>`; mock mode của app bật theo `navigator.webdriver` ⇒ phải `--disable-blink-features=AutomationControlled`): chọn câu → Bấm để nói → Dừng → Chép lời **3–7s** (bản chép thật, có số đo) → sửa → "Chấm bài này · miễn phí (còn N)" → **8–9s** → "Bài của bạn" 7/7 tiêu chí kể cả trôi chảy 4/5 (đo). Ảnh `shots/try/T1–T6`. 🔎 **Bug hiển thị lộ ở ảnh:** `SpeechMetricsPanel` in `silenceRatio` kèm "%" thẳng, BE gửi tỉ lệ 0–1 ⇒ "0.1%" thay 11,5% — admin adapter ×100; **màn kết quả luyện B2C đang mang đúng lỗi này** (spawn task riêng, không sửa lây). Harness lượt đầu bắt nhầm nhãn "Bài của bạn" (2 chỗ) → chờ response POST rồi tìm `section[aria-label]`; lượt bị ngắt để lại lượt `Running` ⇒ 409 tới 5' (self-heal), hết 5 lượt free ⇒ nút tắt + nhãn "Hết 5 lượt cho bản này — lưu bản mới để có thêm lượt" (bump BE/vi → v7 để lấy lượt).
- **Nút đầu trang mở tab thử (user: "1 cái nút mở ra tab test này ở trên đầu trang"; FE `43a3a338`):** trang rubric tách **2 tab** "Mốc điểm | Tự thử thước đo" (`role=tablist`), header có nút `🎙 Tự thử thước đo` (secondary, chỉ hiện khi chưa ở tab thử) nhảy sang tab; tab ghim URL **`?tab=try`** (replace) để F5/chia sẻ link mở đúng chỗ; tab thử chỉ render panel (bảng mốc + lịch sử ở tab kia). Test trang 9 (`renderPage(initialEntries)`), e2e bấm nút header kiểm URL; ảnh `shots/try/U1` (header: `🎙 Tự thử thước đo | Về bản gốc của hệ | Lưu phiên bản mới`, tabs dưới dòng phiên bản) · `U2` (tab thử trên dev, `?tab=try`).

### Đợt B còn lại (user: "làm đợt B đi") — B4 · B10 · `scoringMethod` · nav nhóm · link "tự thử" từ khe chấm
- **BE `feat/admin-prompt-defaults` +4 commit** (`196d6a4` B4 · `c54e2a5` scoringMethod · `51c56f6` B10 · `059e935` ma trận), dev đang chạy `059e935`.
  - **B4** `prompt_templates.updated_by_email` (varchar 256, nullable — mẫu `ranking_overrides.actor_email` E11c): controller đọc claim `"email"` (Interview `MapInboundClaims=false` ⇒ khoá literal, Auth phát `JwtRegisteredClaimNames.Email`) → snapshot lúc ghi, KHÔNG tra Auth (GEN-3); `null` = hàng cũ / thiếu claim, không suy từ `updated_by`. Trả ở cả 3 đường đọc (list · history · upsert). Migration `AddPromptTemplateUpdatedByEmail` **đã apply dev TRƯỚC deploy** (script `--from --to` đọc trọn, additive), **CHƯA apply prod**.
  - **`scoringMethod`** (`Ai | DeliveryMetrics`) trên `AdminRubricCriterionItem` — hết cảnh "Độ trôi chảy" 0 mốc bị FE báo thiếu. Kéo theo **sửa ma trận + lịch sử**: `criteriaCount`/`withLevelsCount` chỉ đếm tiêu chí `Ai` (trước đếm cả 7 ⇒ prod, nơi trôi chảy 0 mốc, sẽ MÃI hiện "6/7 thiếu mốc"). Dev sau vá: `6/6 · đủ mốc` cả 6 ô.
  - **B10** `[JsonUnmappedMemberHandling(Disallow)]` trên **20 request DTO** của action có `[Authorize(Roles="Admin")]` với Admin là role DUY NHẤT (Interview 6 · Payment 10 · Auth 3; **không** áp `DeliveryMetricsDto` — dùng chung đường callback; **không** áp endpoint 3 role Candidate/Employer/Admin — có client mobile) + **guard reflection** `AdminRequestDtoDisallowTests` ở 3 test project (quét controller → suy body param → đòi attribute; Interview thêm test kiểu lồng vì attribute KHÔNG cascade). Khoá lạ ⇒ `JsonException` ⇒ `[ApiController]` 400.
  - Gate: build 0 error · Interview **1777** · Payment **622** · Auth **241** · Campaign 1495 · has-pending No changes · mutation `mutate_b.py` **3/3 ĐỎ** (gỡ snapshot email · gỡ Disallow · đếm cả 7 ở ma trận). Docs: `interview.md`/`payment.md`/`auth.md` + AGENTS copy Interview (sửa có nhắm).
- **FE `fix/admin-config-a` +3 commit** (`a39fbd72` email+link · `136ffddc` scoringMethod UI · `49a43911` nav nhóm) ⇒ **17 commit**.
  - Lịch sử prompt in `updatedByEmail`; hàng cũ có `updatedBy` nhưng không email ⇒ "Không rõ người sửa" (tooltip Guid), không có ai ⇒ "Hệ thống". Editor khe **ảnh hưởng điểm** có nút `🎙 Tự thử thước đo` → `/admin/rubrics?tab=try`.
  - Rubric: badge **"Hệ tự đo"** (tooltip giải thích chấm từ số đo im lặng/nhịp nói) cho `DeliveryMetrics`; ô mốc của tiêu chí đó: 0 mốc ⇒ "không cần mốc" (muted, không phải cảnh báo), có mốc ⇒ "mốc chỉ để giải thích bậc, không tính điểm"; cảnh báo "cần ≥2 mốc" trước khi thử **bỏ qua** tiêu chí đo; parser thiếu/lạ ⇒ `Ai` (chiều an toàn: đòi mốc thừa hơn bỏ sót).
  - Sidebar admin **3 cụm** VẬN HÀNH (dashboard · người dùng · tổ chức · chiến dịch) · TIỀN (Ví & Postpaid) · CHẤT LƯỢNG AI (thước đo · hướng dẫn AI · ngưỡng lộ trình), `role=group` + `aria-label`, đường kẻ giữa cụm (tên mục đã đổi ở đợt A, đợt này chỉ nhóm).
  - Gate: tsc 0 · i18n 16 file · ui-size · radius · **vitest 1877** (293 file) · vite build · mutation `mutate_b_fe.py` **6/6 ĐỎ**.
- **L3 dev:** API `l3_b.py` **10/10** (PUT prompt → `updatedByEmail=admin@isas.local` ở cả 3 đường đọc, hàng cũ null · khoá lạ ⇒ **400 ở cả 3 service**, đối chứng khoá đúng vẫn qua binding (404 user lạ) · `scoringMethod` BE/vi = 6 Ai + 1 DeliveryMetrics · ma trận 6/6) + trình duyệt `shots/dotb.mjs` **7/7**, 0 console error, 0 HTTP ≥400 (ảnh `shots/dotb/B1–B4`: sidebar 3 cụm · nút tự thử ở khe chấm với href đúng · lịch sử sau lưu in email, 2 hàng cũ "Không rõ người sửa" · badge "Hệ tự đo" + ma trận 6/6). Dấu vết dev: `questions.intro` v1 và `seniority.Senior.profile` v3 (có email) đều đã hạ cờ, dev **không còn khe tuỳ chỉnh**.
- ⚠ Harness: Playwright phải chạy từ thư mục FE (`playwright` resolve theo cwd) và launch `--disable-blink-features=AutomationControlled` (mock mode theo `navigator.webdriver`); nút Lưu đòi ghi chú nên phải `fill('#prompt-note')`. `deploy-dev.sh` chỉ đọc label + gateway 200 ⇒ tự kiểm `docker inspect` State/RestartCount sau deploy (bài học aiapi crash-loop cùng ngày).

### Đợt D (user chốt 2026-09-16: "1 2 3 đơn hàng thôi · 4 và 5 màn riêng · 6 trong dashboard") — 5 màn mới + Dashboard, 2026-09-17

Đo lại trên code trước khi làm: BE có **24 endpoint admin FE gọi 0 lần**, dây client (`adminPayment.service.ts` / `adminInterview.service.ts`) đã có 100% ⇒ đợt D thuần dựng màn. Spot-check agent trước ExitPlanMode tìm ra **1 lỗi BE đang sống** (ngừng bán gói mặc định → **500**) + 2 lỗ UI-sẽ-hỏng (gói đã ẩn không bật lại được từ UI vì list lọc `IsActive`; sửa tier reset `entitlementsJson` về `[]` im lặng vì `PlanRequest.ApplyTo` ghi đè mà response không trả) ⇒ 3 sửa BE nhỏ (Payment, 0 migration) đi cùng nhánh `feat/admin-prompt-defaults`.

**FE `fix/admin-config-a` +10 commit (17→27):**
- **Bước 1 hợp đồng** (`f755c33e`): `CreateKnowledgeInput.jobCategory` bắt buộc · `ingestContext7` trả **một** `KnowledgeSource` (BE trả object, FE khai mảng = bảng 0 dòng im lặng) · tách `PackageInput` → `Create/UpdatePackageInput` đúng shape 2 DTO `Disallow` · `PlanInput` giữ `entitlementsVersion` · `CreditTransaction`/AiUsage/Traffic hết `Record<string,unknown>` · type mới `PostpaidOverviewRow`/`GrantCreditResult`/`SubscriptionGrantResult` · **bảng tra enum SỐ Payment** (`adminBilling.ts`: OrderStatus 1..6 · OrderKind · Reason 0..4 · Audience · Funding · PackageType 1..2 · SubscriptionStatus/Source · AlertLevel · `payoutStatus` là CHUỖI) — giá trị lạ → "Không rõ", không in số thô.
- **Bước 2** (`6443e58c`): `OrgPicker` (chọn tổ chức theo tên, `?search=` xuống BE, value ngoài danh sách vẫn hiện option id rút gọn) + `OwnerPicker` (Org theo tên / User theo email ≥3 ký tự); `useAdminUsers += options.enabled` (test lộ hook bắn request rỗng lúc mount).
- **D6 Dashboard** (`07a78874`): Chi phí AI **cùng kỳ với Doanh thu** — `useAdminAiUsage` là dependent query lấy `from/to` từ `revenue.data`, VND từ `revenue.aiCostVnd`; "Tiền đi đâu" top 6 operation; Traffic FR18 (5xx badge, top 5 route). Mutation 4/4 — M1 xanh lượt đầu vì `range!` ném **trước** khi gọi service; làm lại đúng hình dạng lỗi (gọi với `from/to` undefined) ⇒ 2 ĐỎ + thêm assert "mọi lời gọi đều có from/to chuỗi".
- **D2 Ví & Postpaid** (`14eeccae`): `PostpaidWorklist` từ `postpaid-overview` (sort `alertLevel` desc → tiền desc, join tên org), `OrgPicker` thay ô GUID (gỡ 5 khoá i18n chết), `CreditLedger` keyset (`useInfiniteQuery`, limit 20). Test viết lại 9 ca; mutation 5/5.
- **D3 Đơn hàng** (`552b511b`): chỉ xem, filter status/ownerType/refundSettlement (đổi filter reset cursor; bỏ lọc Đã hoàn gỡ luôn refundSettlement), cột Hoàn tiền hiện **lệnh chi hỏng + lý do** (`payoutStatus=Failed`). Mutation 5/5.
- **D1 Kho tri thức** (`340774be`): bảng keyset + lọc nghề, Thêm nguồn URL/Dán tay (chỉ gửi field của loại), Context7 (tìm → chọn 1 → chủ đề → nạp), Nạp lại/Xoá qua ConfirmDialog; `knowledgeErrorMessage` tách 400 `{error}` / 429 Retry-After / 502. Mutation 6/6 (M2 xanh vì test chưa sang trang 2 — lỗ test, bổ sung).
- **D4 Cấp credit/thuê bao** (`96908df2`): `useIdempotencyKey` neo **vân tay form** + `rotate()` sau thành công (BE khớp khoá không xét credits/planId — Q14); thuê bao lọc gói theo chủ ví; 400 "chủ ví chưa có ví" hiện nguyên câu BE + gợi ý. Mutation 6/6.
- **BE-D1/D2/D3** (`0a3b11a`/`7e262fe`/`3437827`): `GET /package?includeInactive=true` **chỉ Admin** (`User.IsInRole`) · `DELETE /admin/plans/{id}` gói mặc định **400** thay 500 · `PlanResponse += EntitlementsJson`. Payment **622→627**, mutation 4/4 (bỏ guard Admin → đỏ = người lạ thấy giá chưa công bố). Deploy dev verify bằng hành vi: DELETE `free` → 400 message; response có `entitlementsJson`.
- **D5 Gói & Tier** (`f2cef854`): 2 tab `?tab=`; `buildPlanRequest` **strip theo audience/cờ** (B2C null 3 trần B2B + postpaid=false · adaptive tắt ⇒ cap null · không Metered ⇒ quota null) + echo entitlements; gói mặc định không có nút Ngừng bán; Gói bán: OneTime không mang planId/audience, Update không mang type, Ẩn = DELETE, công tắc gói ẩn + Bán lại. Mutation 8/8.
- **Sau L3** (`6c6d71ac`): cột `$` bảng "Tiền đi đâu" bị cắt (`2,30 US$` trong card 1/3) → `$2.30`; ô Trần tier NBSP.

**Gate cuối:** FE **vitest 1927 / 305 file** (từ 1877) · tsc · i18n 16 · ui-size · radius · build · **BE Payment 627** (+5), has-pending No changes, 0 migration. **L3 dev (Chrome thật, Playwright): 33/33 PASS** — D6 ai-usage query `from/to` **==** revenue response của chính trang, ₫ == `aiCostVnd`; D5 tạo gói → public 0→1 → Ẩn → 0 → công tắc → thấy "Đã ẩn" → Bán lại → 1; tạo tier B2C (BE nhận `[]`, seats null) → sửa giữ `entitlementsVersion` → ngừng bán `isActive=false`; free chỉ có "Sửa"; D4 cấp 5 → ví +5, **BE idempotent qua API: 2 POST cùng khoá → cùng `transactionId`, ví +1 không +2**; thuê bao B2B → `starter · Đang hiệu lực · Cấp tay · từ→đến +30d`; org mới chưa ví → 400 nguyên câu + gợi ý; D2 worklist 0 → duyệt Postpaid 50 → 1 dòng đúng org, sổ cái hiện `Cấp tay +5`; D3 đơn PayOS sandbox 201 → "Chờ trả" → huỷ 204 → lọc "Đã huỷ"; D1 Manual BA → +1 (2 chunk) → Nạp lại → Xoá về số cũ; 375px 6 màn không tràn ngang. HTTP ≥400 duy nhất là ca 400 cố ý.
- ⚠ **Harness sai 5 lần trước khi app sai 0 lần** (ghi để không lặp): route login là `/login` (modal 2 form — neo form có `autocomplete=current-password`) không phải `/auth/login`; `getByLabel('Tổ chức')` trúng **link nav** `aria-label="Tổ chức"` trước select ⇒ dùng `getByRole('combobox')`; so kỳ ai-usage với lời gọi revenue **riêng của harness** (mốc "now" khác) thay vì response của trang; `option.waitFor()` chờ visible trong select đóng ⇒ `state: 'attached'`; tên gói/tier phải duy nhất giữa các lượt chạy.
- ⚠ **Không đảo lại được trên dev:** org demo `25eb4547` nay **Postpaid hạn mức 50** — hạ về Prepaid bị **409** vì guard BE `reserved_credits > 0` (2 chỗ giữ cũ), **và câu báo của BE không nhắc điều kiện này** (chỉ nói hoá đơn/period_usage) — backlog nhỏ. Dấu vết dev còn: 1 gói active `L3-D5 Gói 5 credit 27585` (100k/5 credit — catalog dev hết "0 gói"), 4 gói L3 đã ẩn, 5 tier L3 đã ngừng, 2 org `L3 Org *` không ví, 1 đơn `Đã huỷ`, ví org 43 credit (+18 từ L3), thuê bao Starter 30 ngày cho org demo.

**Trạng thái cuối ngày:** BE `feat/admin-prompt-defaults` **13 commit** (`ee47f4c`→`3437827`, dev đang chạy) · FE `fix/admin-config-a` **28 commit** off `origin/dev` — **cả hai CHƯA push, CHƯA PR**. Đợt A + B + D **xong**; đợt C (công tắc `platform_settings`) hoãn. ⚠ **Trước merge `main`: apply `AddPromptTemplateUpdatedByEmail` lên `postgres-main/isas_interview`** (additive; CI tự deploy khi merge — sự cố `42703` ×4); đợt D **0 migration**. **Thứ tự merge: BE trước, FE sau** (FE phụ thuộc `scoringMethod`/`updatedByEmail`/transcribe/`includeInactive`/`entitlementsJson`). Còn chờ user: push + PR; `ExpectedLevels` Yếu→mốc 2 hay đổi câu verdict; đưa luồng nói sang employer FE; đợt C; venv 3.14 vs image 3.12; bug "%" `SpeechMetricsPanel` (task đã spawn); hoàn tiền/payout/settle chưa có UI (cố ý ngoài phạm vi D).
