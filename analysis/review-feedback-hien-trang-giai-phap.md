# Phản hồi review — Hiện trạng → Vấn đề → Giải pháp

> **Nguồn:** góp ý review dự án ISAS (SU26SE043).
> **Đo trên:** `main` @ `b7957d8` (2026-08-17) — mọi mục **Hiện trạng** đều đọc code thật kèm `file:line`, **không** trích `progress.md`.
> **Cách đọc:** mỗi mục = *Hiện trạng* (code đang làm gì) → *Vấn đề* (vì sao sai) → *Giải pháp* (đổi cái gì).
> **Ký hiệu:** 🔴 chặn nghiệp vụ · 🟠 sai kết quả · 🟡 thiếu/UX · ✅ đã có (chỉ cần nối FE hoặc bật cờ).

---

## 0. Bảng tổng hợp

| # | Vấn đề | Mức | Trạng thái thật |
|---|---|---|---|
| A1 | JD chưa sinh ra **bộ tiêu chí bắt buộc / nên có** | 🔴 | Có `job_needs` nhưng **không có** nhãn bắt buộc |
| A2 | Điều kiện loại (`RequiredSkills`…) **không có đường khai** | 🔴 | Cột + hàm lọc có, **0 DTO/endpoint/UI** |
| A3 | Mọi tiêu chí **trọng số bằng nhau** | 🟠 | `jobFitScore` = trung bình đều |
| B1 | Điểm phân tích CV (B2C) **do AI phán** | 🟠 | `jdMatch.score` model trả thẳng |
| B2 | CV chưa list **theo từng tiêu chí** (B2C) | 🟠 | Chỉ 3 mảng text rời |
| B3 | B2C vẫn **chấm điểm** CV | 🟡 | Cần bỏ điểm, đổi sang must/nice-to-have |
| B4 | **Không suy level từ CV** | 🔴 | `seniority` = người dùng **tự khai** |
| B5 | Level **không có định nghĩa định lượng** | 🟠 | Chỉ mô tả chữ trong prompt |
| C1 | B2C bật ghi âm nhưng **không có anti-cheat** | 🟡 | Đúng thiết kế BC-6 — cần chốt lại |
| C2 | Anti-cheat B2B **lỗi false-positive** | 🔴 | Ảnh mốc đen → `face_mismatch` oan |
| D1 | Muốn **bỏ follow-up**, nộp là chấm luôn | 🟠 | Adaptive đang bật, 5 gốc × 3 sâu |
| D2 | Độ ổn định model đọc (transcript) | 🟠 | Đã có cổng im lặng + chống rác |
| D3 | **Chưa hoàn tất phỏng vấn** — chưa xử đủ | 🟠 | Có sweeper, thiếu UX + báo cho HR |
| D4 | Bỏ **repo analysis**; thêm **xuất report** | 🟡 | Repo có; B2C **0 endpoint export** |
| E1 | Roadmap **import report người dùng chọn** | ✅ | BE xong (BC17), **FE chưa nối** |
| E2 | CV có kinh nghiệm → roadmap **mới tốt nghiệp** | 🔴 | Hệ quả trực tiếp của B4 |
| E3 | Roadmap chưa chia **sửa lỗi / lên level** | 🟠 | AI tự do bố cục |
| E4 | Cập nhật **status lesson** | ✅ | Enum có, FE chưa hiện |
| F1 | Câu hỏi campaign gen từ JD | ✅ | Đã bắt buộc có JD |
| F2 | **Trọng số từng câu hỏi** + công thức điểm | 🔴 | `campaign_questions` **không có cột weight** |
| F3 | Upload file câu hỏi kèm trọng số | 🟡 | Chưa có |
| G1 | Công thức **ranking list CV** | 🟠 | Trung bình đều, không must-have |
| H1–H6 | Bớt quăng file · UI · doc · quán triệt luồng | 🟡 | Xem §H |

---

## A. JD → bộ tiêu chí chấm điểm

### A1. JD phải sinh ra bộ tiêu chí, trong đó có tiêu chí **bắt buộc**

**Hiện trạng.** Đã có bước "suy nhu cầu công việc từ JD" — `campaigns.job_needs` (jsonb), materialize **một lần lúc publish** cho cả campaign, HR sửa được khi `Draft`. Mỗi dòng là:

```
JobNeed { NeedId, Category, Text, Source }      // Models/JobFitScreening.cs:15-25
JobNeedCategories = Technical | WorkStyle | Communication | Growth
```

Chưa chốt `job_needs` thì **không sàng CV được** (test `Chua_chot_job_needs_thi_khong_sang_duoc`).

**Vấn đề.** `JobNeed` **không có trường nào phân biệt bắt buộc với nên có**. `Category` chỉ nói *nhu cầu thuộc nhóm nào*, không nói *thiếu nó thì loại hay trừ điểm*. Hệ quả ở `CvScreeningService.cs:236`:

```csharp
100m * assessments.Sum(a => NeedLevels.Credit(a.Level)) / assessments.Count
```

→ nhu cầu **"3 năm Java"** và nhu cầu **"chủ động học hỏi"** đóng góp **y hệt nhau**. Ứng viên thiếu đúng thứ bắt buộc vẫn có thể xếp trên ứng viên đủ, chỉ cần gỡ điểm ở mấy nhu cầu mềm.

**Giải pháp.**
1. Thêm vào `JobNeed`: `Importance ∈ { MustHave, NiceToHave }` (**server sở hữu** như `Source` — client gửi bị bỏ qua, theo tiền lệ F10).
2. AI bước 1 tự đề xuất `Importance` khi đọc JD; HR sửa được lúc `Draft` (CAMP-2).
3. `MustHave` **không nhập vào điểm dưới dạng trừ điểm** — nó là **cờ loại** (xem A3/G1).
4. Migration additive (`job_needs` là jsonb ⇒ **không cần DDL**, chỉ cần default `NiceToHave` cho dòng cũ).

---

### A2. Điều kiện loại đã có cột nhưng **không có đường khai** (dead feature)

**Hiện trạng.** Ba trường lọc cứng tồn tại đầy đủ ở tầng dữ liệu và tầng nghiệp vụ:

```csharp
// Models/Campaign.cs:49-51
public List<string>? RequiredSkills { get; set; }   // phải có ĐỦ
public List<string>? KeywordsAny  { get; set; }     // có ≥1
public int? MinYearsExperience    { get; set; }
```

và hàm lọc chạy thật ở `CampaignService.cs:1729-1746` (thiếu → `Rejected` kèm lý do).

**Vấn đề.** `grep` trên `DTOs/` và `Controllers/` ra **rỗng** — **không có DTO, không có endpoint, không có UI** để HR khai ba trường này. Chúng vĩnh viễn `null` ⇒ nhánh lọc **không bao giờ chạy**. Đây là "có tên mà không có ruột": nhìn code tưởng đã có sàng lọc bắt buộc, thực tế **0 campaign** dùng được.

**Giải pháp.**
1. Đưa 3 trường vào `CreateCampaignRequest` / `UpdateCampaignRequest` + `CampaignResponse`.
2. UI Employer: mục **"Điều kiện bắt buộc"** ở form campaign.
3. Hợp nhất ngữ nghĩa với A1: `MustHave` của `job_needs` là **đánh giá có bằng chứng** (AI đọc CV), còn `RequiredSkills` là **lọc từ khoá thô** (rẻ, chạy trước, không tốn token). Giữ cả hai, khác vai:
   - `RequiredSkills`/`MinYearsExperience` → **lọc trước khi gọi AI** (`Rejected`, tiết kiệm tiền).
   - `MustHave` job need → **sau khi AI đọc CV**, thiếu bằng chứng ⇒ đánh dấu **"Không đạt điều kiện bắt buộc"**.

---

### A3. Mức độ quan trọng của từng tiêu chí

**Hiện trạng.** `jobFitScore` là **trung bình đều** trên toàn bộ `assessments` (`CvScreeningService.cs:233-236`), mức quy đổi `Strong=1 · Partial=0.5 · Weak=0` (`NeedLevels.Credit`).

**Vấn đề.** Trung bình đều là lựa chọn **có chủ đích và đúng ở thời điểm đó** — comment trong code ghi rõ: *"không có dữ liệu nói technical đáng gấp mấy lần communication, bịa hằng số rồi trưng ra như chuẩn ngành"*. Nhưng nó chỉ đúng khi **HR không được khai gì**. Khi HR **tự khai** mức quan trọng thì đó không còn là hằng số bịa — đó là **đầu vào nghiệp vụ**.

**Giải pháp.**
1. Thêm `Weight` (hoặc `Importance ∈ {Critical, High, Normal}` → map ra hệ số) vào `JobNeed`, **do HR khai**, mặc định đều nhau.
2. Công thức mới:
   ```
   jobFitScore = 100 × Σ(Credit(level) × weight) / Σ(weight)
   ```
3. **Không** để AI trả trọng số — cùng lý do đã bịt ở F10/`jobFitScore`: số nào dùng để xếp hạng thì **code tính**, AI chỉ trích bằng chứng.
4. Đóng dấu `screening_version = 3` (thang mới **không so sánh được** với thang 2 — tiền lệ `scoring_scope_version`/BK23).

---

### A4. Nhập tay JD → AI tách bắt buộc / nên có

**Hiện trạng.** JD nhập được bằng **text trực tiếp** (`jdText`) hoặc PDF; gửi cả hai thì **text thắng, bỏ file** (C11). Ngưỡng 20.000 ký tự. AI đọc JD để sinh `job_needs` và sinh câu hỏi.

**Vấn đề.** Luồng đã đúng, **thiếu đúng bước phân loại** ở A1 — AI hiện chỉ trả `Category`, không trả `Importance`.

**Giải pháp.** Sửa prompt bước 1 (`suggest job needs`) trả thêm `importance`; server **validate + ghi đè `Source`**; HR review trên UI trước khi publish. Không đổi luồng nhập JD.

---

## B. Phân tích CV

### B1. "Điểm đó từ đâu ra?" — B2C vẫn để AI phán

**Hiện trạng.** Hai đường **khác hẳn nhau**:

| | B2C (`/practice/cv-analysis`) | B2B (sàng CV campaign) |
|---|---|---|
| Điểm | `jdMatch.score` — **model trả thẳng**, code chỉ kẹp `[0,100]` (`gemini.py:850`) | `jobFitScore` — **code tính** từ mức bằng chứng |
| Bằng chứng | Không có | `evidence` = **đoạn trích từ CV** |
| Con dấu | Không | `screening_version` |

**Vấn đề.** Đường B2B **đã sửa** (bản 2026-08-14, lý do: đo trên prod 4 CV có bằng chứng giống hệt nhận 70/70/55/55). Đường **B2C thì chưa** — người luyện tập nhận một con số 0-100 mà **không ai giải thích được nó ở đâu ra**, và nó **không tái lập** giữa hai lần chạy cùng một CV.

**Giải pháp.** Xem B3 — với B2C, cách sửa đúng **không phải** là tính lại điểm cho chuẩn, mà là **bỏ điểm đi**.

---

### B2. Tiêu chí chấm CV phải **list ra theo từng tiêu chí**

**Hiện trạng.**
- B2B: **đã có** — `NeedAssessment { NeedId, Area, Level, Evidence }`, mỗi nhu cầu một dòng, không tìm thấy bằng chứng thì ghi đúng hằng số `"Không thấy bằng chứng"` (phân biệt *đã tìm và không thấy* với *quên đánh giá*).
- B2C: **chưa** — chỉ có `strengths[] / weaknesses[] / suggestions[]` là ba mảng **text rời**, không neo vào tiêu chí nào (`schemas.py:148-153`, `Entities/CvAnalysis.cs:24-26`).

**Vấn đề.** Ở B2C, người dùng đọc "điểm mạnh: giao tiếp tốt" mà **không biết nó được đối chiếu với yêu cầu nào của JD**, cũng không biết cái gì còn thiếu để đạt.

**Giải pháp.** Áp cấu trúc **có bằng chứng** của B2B sang B2C, đơn giản hoá cho cá nhân:

```
requirements[]: { text, importance: MustHave|NiceToHave, met: Yes|Partial|No, evidence }
```

`evidence` **bắt buộc là trích từ CV**, không phải câu AI tự viết — đây là thứ khiến kết quả kiểm chứng được.

---

### B3. B2C **bỏ chấm điểm CV**

**Hiện trạng.** `AnalyzeCvResponse = { summary, strengths[], weaknesses[], suggestions[], jdMatch{score, matchedSkills[], missingSkills[]} }`.

**Vấn đề.** Điểm số ở đây **không phục vụ quyết định nào**: B2C không xếp hạng ai với ai, không có ngưỡng pass/fail. Nó chỉ tạo cảm giác chính xác giả — và như B1 đã chỉ, nó không tái lập.

**Giải pháp.** Đổi response B2C thành 4 khối, **bỏ `score`**:
1. **Điểm mạnh** (có bằng chứng trong CV)
2. **Điểm yếu / còn thiếu**
3. **Bắt buộc phải có để phù hợp JD** — thiếu là bị loại
4. **Nên có để gây ấn tượng với HR** — có thì nổi bật hơn

Giữ `matchedSkills`/`missingSkills` (chúng là **sự kiện**, không phải phán xét). ⚠ Đây là **breaking change** với FE → phải sửa lockstep 2 repo.

---

### B4. Phân tích **level** của ứng viên từ CV 🔴

**Hiện trạng.** `seniority` tồn tại đầy đủ ở cả 3 tầng (`practice_sessions.seniority`, `campaigns.seniority`, CHECK ở DB, đi vào prompt sinh câu hỏi qua `app/seniority.py`). Nhưng nguồn của nó là:

> `seniority` là lựa chọn của **NGƯỜI DÙNG** (B2C ứng viên tự khai / B2B do HR đặt cấp chiến dịch)
> — `app/seniority.py:3-4`

Phân tích CV **không suy ra level** và **không ghi vào đâu**.

**Vấn đề.** Đây là **gốc rễ của E2** (roadmap sai level) và làm sai độ khó câu hỏi. Người dùng 5 năm kinh nghiệm chọn nhầm "Junior" (hoặc để mặc định — mặc định **là** `Junior`) thì toàn bộ hệ thống phía sau chạy sai: câu hỏi quá dễ, roadmap dạy lại thứ họ đã biết, điểm không phản ánh gì.

**Giải pháp.**
1. Phân tích CV trả thêm `detectedLevel ∈ {Fresher, Junior, Middle, Senior}` **kèm bằng chứng** (số năm, quy mô dự án, vai trò).
2. Lưu vào `cv_analyses.detected_level`.
3. Khi tạo buổi luyện / roadmap: **gợi ý sẵn** level từ CV, người dùng **được đổi** nhưng thấy rõ "CV của bạn cho thấy Middle".
4. **Không tự động ép** — CV có thể cũ/thiếu; ép sẽ thành một cái sai không sửa được.

---

### B5. Định nghĩa **định lượng** cho level

**Hiện trạng.** 4 mức có tồn tại (`app/seniority.py`, CHECK ở DB), phân biệt bằng **đoạn mô tả chữ trong prompt** do agent soạn — chưa hiệu chuẩn bằng dữ liệu nào.

**Vấn đề.** Không có ranh giới đo được ⇒ hai lần chạy cùng một CV có thể ra hai level; và không ai giải thích được vì sao.

**Giải pháp.** Chốt **quy tắc định lượng** (team quyết, ghi vào `docs/rules.md`), ví dụ:

| Level | Năm KN | Dấu hiệu bắt buộc |
|---|---|---|
| Fresher | < 1 | Chưa có dự án thật (chỉ đồ án/thực tập) |
| Junior | 1–2 | Có dự án thật, làm theo yêu cầu có sẵn |
| Middle | 2–5 | Tự thiết kế module, có dấu hiệu tự chủ kỹ thuật |
| Senior | > 5 | Dẫn dắt/định hướng kỹ thuật, mentor, quyết định kiến trúc |

Suy level = **code quyết** từ các dấu hiệu AI trích ra (giống `jobFitScore`), **không** hỏi model "đây là level gì".

---

## C. Anti-cheating

### C1. "Bật cam thì phải check, không check thì đừng bật"

**Hiện trạng.**
- **B2C luyện tập**: `grep` webcam/proctor ở `features/candidate/practice/` → chỉ có `audio-recorder.ts`. **Không bật camera, không có anti-cheat.** Phía BE cũng vậy (`grep SessionFlag` trong `Isas.InterviewService` = **rỗng**). Đúng luật BC-6 hiện hành ("B2C là luyện tập, không phải thi").
- **B2B campaign**: có camera + face verify + cờ hành vi.

**Vấn đề.** Nguyên tắc trong góp ý là đúng và cần **ghi thành luật**: bật camera mà không kiểm là thu dữ liệu sinh trắc **không có mục đích sử dụng** — vừa vô ích vừa phải xin consent (DATA-3).

**Giải pháp.** Chốt tường minh trong `docs/rules.md`:
> Camera chỉ được bật khi có **đường xử lý kết quả** (đối chiếu khuôn mặt → sinh cờ → HR đọc được). Không có đường đó thì **không bật camera**.

Với B2C: **giữ nguyên không camera** (đúng bản chất luyện tập). Nếu sau này muốn có "chế độ thi thử nghiêm túc" thì bật cả cụm, không bật lẻ camera.

---

### C2. Anti-cheat đang **lỗi** 🔴

**Hiện trạng.** Cơ chế có đủ và **chạy thật**: 4 cờ hành vi (`tab_switch`/`paste`/`focus_lost`/`camera_blocked`) + 4 cờ danh tính từ AIService (`face_mismatch`/`no_face`/`multiple_faces`/`identity_unverified`), phân quyền chặt (ứng viên **không** tự cắm được cờ danh tính — `SessionFlagController.cs:47-51`).

**Vấn đề** — hai lỗi đã tái hiện được:
1. **Ảnh mốc (reference) chụp lúc camera chưa phơi sáng → khung đen** ⇒ so khớp trả score 0 ⇒ gắn **`face_mismatch`** cho ứng viên trung thực, lặp **mỗi 30 giây**. Đo được: ảnh mốc `sáng = 0.0/255`, 2.5KB; đem so **với chính nó** cũng ra `no_face`.
2. **Nhãn sai bản chất**: ảnh mốc hỏng phải là **`identity_unverified`** (lỗi kỹ thuật) chứ không phải `face_mismatch` (**cáo buộc có người khác ngồi thay**). Hệ thống hiện **không phân biệt hai ca này** — mà chúng dẫn tới hai quyết định hoàn toàn khác nhau của HR.

**Giải pháp** (đã có bản vá ở nhánh `fix/face-verify-unusable-reference`, **chưa merge**):
1. **FE**: đo độ sáng **và độ lệch chuẩn** khung hình trước khi gửi ảnh mốc; chưa dùng được ⇒ **không upload** + nhắc "bật thêm đèn, ngồi vào giữa khung" + thử lại nhịp sau. *(Phải đo cả độ lệch chuẩn: prod đang có 2 ảnh mốc `sáng=128` — xám đồng nhất, sáng "vừa đẹp" mà không có mặt nào.)*
2. **BE/AI**: mốc không đọc được mặt ⇒ trả **`identity_unverified`**, không phải `face_mismatch`.
3. Cân nhắc chặn ngay tại `face-enroll` (server hỏi AIService "ảnh này có đúng 1 mặt không" trước khi nhận làm mốc) — đóng ở tầng server thay vì tin FE.
4. ⚠ **Phép nghiệm thu bắt buộc là đối chứng ngược**: sau khi có mốc tốt, **người khác ngồi thay vẫn phải ra `face_mismatch`** — nếu không thì bản vá đã giết luôn tính năng.

**Giới hạn cần nói đúng:** `multiple_faces` nghĩa là *phát hiện ≥2 khuôn mặt*, **không phải đếm đủ số người trong phòng** (đo thật: ảnh 5 người, quay đi/che khuất → chỉ bắt được 3). Đừng hứa với hội đồng là hệ thống đếm được người.

---

### C3. Tham gia campaign — anti-cheat "full options"

**Hiện trạng.** Bật/tắt theo campaign qua 2 cờ: `anti_cheat_enabled`, `face_verify_enabled`.

**Giải pháp.** Sau khi vá C2, rà lại theo checklist: ①ứng viên **bỏ ngang** thì cờ vẫn tới HR (đã có `unscoredFlagged`, **FE chưa hiển thị** — xem H3) · ②gộp cờ trùng loại liên tiếp (buổi 30' có thể sinh ~60 dòng cùng loại) · ③ảnh khuôn mặt có **retention** (đã có bảng `face_images`, cần bật job dọn).

---

## D. Luồng phỏng vấn

### D1. Bỏ follow-up — nộp bài là chấm luôn

**Hiện trạng.** Phỏng vấn thích ứng (INT-17b) đang bật ở prod: **5 câu gốc**, mỗi câu đào sâu tối đa **3 lần** theo chuỗi (`appsettings.json:50-57`). Sau mỗi câu trả lời, nếu còn ngân sách thì gọi `/decide-next` **đồng bộ trong request upload** (`AnswerService.cs:319`) → sinh câu kế.

**Vấn đề.** Đây là chỗ **đắt nhất và mong manh nhất** của cả hệ: mỗi lượt nộp phải chờ *chép lời (≈3s) + Gemini (≈1,4s) + mạng* ≈ **9,4s**; từng đo được lượt **96s** vượt timeout 90s → rơi về luồng tĩnh. Nó cũng làm **số câu khác nhau giữa các ứng viên cùng campaign** trong khi điểm vẫn đem xếp hạng chung.

**Giải pháp.** Đã có sẵn **kill-switch một khoá**: `Adaptive__MaxDeepPerQuestion=0` → trả lại đúng luồng tĩnh trước INT-17b (sinh sẵn N câu, nộp là đẩy đi chấm ngay, không gọi `/decide-next`).
- **B2B**: nên **tắt** — công bằng xếp hạng quan trọng hơn chiều sâu hội thoại.
- **B2C**: có thể giữ (luyện tập thì hội thoại có giá trị) hoặc tắt để giảm độ trễ — **team chốt**.
- ⚠ Không đổi bằng cách sửa 4 khoá config rời rạc; chỉ đổi đúng `MaxDeepPerQuestion`.

---

### D2. Độ ổn định của model đọc (transcript)

**Hiện trạng.** Đường chép lời đã được siết đáng kể:
- Nhà cung cấp chọn bằng env (`local` / `whisper-1` / `gemini`); prod đang dùng `whisper-1` (**lỗi từ 4,2% → 0,7%**).
- **Cổng im lặng (VAD)**: bản ghi không có tiếng người ⇒ `no_speech`, **không gọi engine nào**, answer `Skipped` — không đốt token, không chấm bừa.
- **Bộ dò rác**: bắt transcript hỏng dạng lặp vòng (vết bẩn dữ liệu huấn luyện Whisper).

**Vấn đề còn lại.** ①Chỉ số vẫn có sai lệch nhỏ (`longestPause` lệch 0,82s) · ②**không có gold set** — chưa bộ câu trả lời nào được **người** chấm để đối chiếu, nên **chưa ai đo được điểm AI đúng hay sai** · ③cỡ mẫu đo mới n=7.

**Giải pháp.**
1. **Dựng gold set** (~20 câu trả lời, người chấm) — đây là việc quan trọng nhất còn thiếu của cả mảng chất lượng chấm điểm, đã hoãn nhiều lần.
2. Mở rộng bộ đo chép lời (S3 còn ~11 file chưa dùng) rồi chốt `whisper-1` hay `gemini`.
3. ⚠ **Không dùng audio tổng hợp (TTS) để kết luận** — đã có tiền lệ: trên audio TTS thứ hạng model **đảo ngược hoàn toàn** so với ghi âm thật.

---

### D3. "Check lại chưa hoàn tất việc phỏng vấn"

**Hiện trạng.** Có sweeper xử buổi treo (quá hạn → tự nộp/bỏ), có trần bỏ cuộc khi chấm kẹt, có hoàn credit khi buổi hỏng trước lúc sinh câu hỏi. Nhưng:
- Buổi B2B bấm **Start rồi đóng tab** từng kẹt `Ready` **vĩnh viễn** (đã vá, hoàn được 4 credit thật sau 3 tuần).
- Ứng viên **bỏ ngang** không bao giờ `Scored` ⇒ **không lên bảng kết quả** ⇒ HR không thấy — mà đó đúng là nhóm đáng nghi nhất.

**Vấn đề.** Trạng thái "chưa hoàn tất" hiện **đúng ở DB nhưng không lộ ra cho người dùng và HR**.

**Giải pháp.**
1. **HR**: hiển thị mục **"Chưa hoàn tất"** trong bảng kết quả campaign (BE đã trả `unscoredFlagged`, **FE chưa render** — xem H3).
2. **Ứng viên**: màn hình luyện phải nói rõ *còn bao nhiêu câu chưa trả lời* và **cảnh báo trước khi nộp** nếu chưa xong.
3. Rà lại toàn bộ chuyển trạng thái buổi thi bằng một bảng **state machine** viết ra giấy, đối chiếu code (thuộc H6).

---

### D4. Bỏ **repo analysis**; thêm **xuất report**

**Hiện trạng.**
- Repo analysis (BC18) có đầy đủ: endpoint, bảng, tính phí ~$0,0095/lượt, cần GitHub token (PAT có hạn 30 ngày, hết hạn ⇒ **mọi lượt trả 400**).
- **B2C không có endpoint xuất report nào** (`grep export` trong `Isas.InterviewService/Controllers` = **rỗng**). B2B thì có (`/results/export`, CSV + PDF).

**Vấn đề.** Repo analysis là chi phí + rủi ro vận hành (token hết hạn, rate limit dùng chung theo IP) cho một tính năng **không nằm trên đường chính** của sản phẩm. Trong khi đó thứ người dùng thật sự cần — **cầm kết quả buổi phỏng vấn về** — thì không có.

**Giải pháp.**
1. **Gỡ repo analysis khỏi luồng phỏng vấn** (ẩn UI trước, gỡ code sau — giữ dữ liệu cũ đọc được).
2. Thêm `GET /practice/sessions/{id}/export?format=pdf|csv` cho B2C: câu hỏi + transcript + điểm từng tiêu chí + nhận xét + chỉ số cách nói. Tái dùng bộ xuất PDF/CSV đã có ở Campaign (đã verify tiếng Việt đủ dấu trên image deploy).

---

## E. Roadmap

### E1. Import report người dùng chọn ✅ *(BE xong, FE chưa nối)*

**Hiện trạng.** BE **đã làm** — `RoadmapService.CreateAsync` nhận `req.SessionIds`, chỉ gom **những buổi ứng viên chọn** (owner-scoped + B2C + đã `Scored`; thiếu id nào → 404 batch). Rỗng ⇒ roadmap chuẩn theo level, **không query buổi nào**.

**Vấn đề.** `grep sessionIds` toàn bộ FE = **rỗng** ⇒ giao diện vẫn đi đường cũ (tự gom mọi buổi) ⇒ **tính năng không tới được người dùng**.

**Giải pháp.** FE: thêm bước **chọn report** khi tạo roadmap (danh sách buổi đã chấm + CV analysis + report roadmap cũ), kèm ô **mục tiêu** (`focus`) mô tả muốn cải thiện gì.

---

### E2. CV có kinh nghiệm nhưng roadmap dạy như mới tốt nghiệp 🔴

**Hiện trạng.** Roadmap sinh từ: buổi luyện đã chấm (điểm yếu) + CV (tuỳ chọn) + level. Mà **level là do người dùng tự khai** (B4) và **mặc định là `Junior`**.

**Vấn đề.** Người dùng không đổi mặc định ⇒ nhận roadmap Junior bất kể CV nói gì. Đây **không phải lỗi của roadmap** — nó là **hệ quả trực tiếp của B4**.

**Giải pháp.** Sửa ở gốc (B4): CV analysis suy `detectedLevel` → tạo roadmap **gợi ý sẵn** level đó → prompt roadmap nhận cả `detectedLevel` lẫn bằng chứng (số năm, vai trò) chứ không chỉ một chữ "Junior".

---

### E3. Cấu trúc roadmap: sửa lỗi trước, lên level sau

**Hiện trạng.** Số milestone và bố cục **do AI tự quyết** — đo thật: BA 4 mốc/12 bài, BE 3/11, FE 4/16. Không có ràng buộc cấu trúc nào.

**Vấn đề.** Không đảm bảo roadmap **bám vào điểm yếu đã đo được**; AI có thể sinh một lộ trình học chung chung không liên quan gì tới report người dùng vừa chọn.

**Giải pháp.** Ép cấu trúc trong prompt + **kiểm bằng code sau khi AI trả về** (mẫu đã dùng cho cổng chất lượng bài giảng):
- **Giai đoạn 1 (2 mốc đầu)** — *khắc phục*: mỗi mốc phải **neo vào ≥1 tiêu chí `needs_improvement`** trong report được chọn.
- **Giai đoạn 2 (2 mốc sau)** — *nâng cấp*: nội dung của **level kế tiếp** so với `detectedLevel`.
- Không phủ được ⇒ **trả lại AI viết lại 1 lượt** rồi mới lưu (không im lặng chấp nhận).

---

### E4. Cập nhật status lesson ✅ *(model có, FE chưa hiện)*

**Hiện trạng.** `LessonStatus: Theory → Practicing → Done` và `MilestoneStatus: Pending → InProgress → Completed` (`Enums/RoadmapEnums.cs:21-33`), chuyển trạng thái đã đấu dây (bắt đầu luyện → `Practicing`; buổi `Scored` → `Done`).

**Vấn đề.** Không hiển thị được tiến độ trên UI ⇒ người dùng không biết mình đang ở đâu.

**Giải pháp.** FE: thanh tiến độ theo milestone + badge trạng thái mỗi bài + đánh dấu bài kế tiếp nên học.

---

## F. Campaign

### F1. Câu hỏi phải liên quan JD ✅

**Hiện trạng.** Đã bắt buộc: `CampaignService.cs:649` — chưa có JD thì **từ chối sinh câu hỏi** (*"Campaign chưa có JD (jdText) — cần JD để AI sinh câu hỏi"*). JD được bọc delimiter chống prompt-injection (AI-4). Nguồn gốc câu hỏi (`Source`) **do server sở hữu**, HR sửa nội dung vẫn giữ nhãn "AI sinh" + cột `HrEditedAt` riêng.

**Kết luận.** Mục này **đã đạt**, không cần làm gì thêm.

---

### F2. **Trọng số từng câu hỏi** + công thức điểm tổng 🔴

**Hiện trạng.**
```csharp
// Models/CampaignQuestion.cs — KHÔNG có cột Weight
Id, CampaignId, OrgId, QuestionText, Source, IsRequired, CreatedAt, HrEditedAt
```
Công thức điểm tổng (`SessionScoringNotifier.cs:220-229`) chuẩn hoá theo **tiêu chí**:
```
pct(c)     = clamp(avgScore(c) / maxScore(c) × 100, 0, 100)
TotalScore = Σ (pct(c) × weight(c)) / Σ weight(c)
```

**Vấn đề.** Trọng số **chỉ có ở tiêu chí**, **không có ở câu hỏi**. Nghĩa là:
- Câu "hãy tự giới thiệu" và câu "thiết kế hệ thống chịu tải 1 triệu request" **đóng góp như nhau** vào điểm tiêu chí *Chiều sâu kỹ thuật*.
- HR **không có cách nào** nói "câu số 3 là câu quyết định".
- ⚠ Lưu ý: `IsRequired` đã có nhưng đó là *"bắt buộc trả lời"*, **không phải** trọng số.

**Giải pháp.**
1. Thêm `campaign_questions.weight` (decimal, mặc định 1 = hành vi hiện tại **không đổi**).
2. Công thức tổng hai tầng — đây là **điểm cần nghiên cứu kỹ nhất** của cả danh sách:
   ```
   pct(c) = Σ_q [ score(q,c)/maxScore(c) × weight(q) ] / Σ_q weight(q)     ← gộp theo CÂU trong 1 tiêu chí
   Total  = Σ_c [ pct(c) × weight(c) ] / Σ_c weight(c)                     ← gộp theo TIÊU CHÍ (giữ nguyên)
   ```
   Chỉ cộng những câu **thật sự được chấm** ở tiêu chí đó (INT-18: tiêu chí không được hỏi thì **loại khỏi điểm, không tính 0**).
3. **Đóng dấu phiên bản công thức** — điểm cũ và mới **không so sánh được**, mà `campaign_rankings` đang xếp chung bảng.
4. Ràng buộc: `weight > 0`; UI hiện tổng trọng số để HR thấy tỉ lệ thật.

---

### F3. Upload file câu hỏi kèm trọng số

**Hiện trạng.** Câu hỏi nhập tay từng câu hoặc AI sinh; **không có** đường import hàng loạt.

**Giải pháp.** Import CSV/Excel: `question_text, weight, is_required, target_criteria`. Bắt buộc **xem trước + xác nhận** trước khi ghi (không import thẳng), validate trọng số > 0, chỉ cho ở trạng thái `Draft` (CAMP-2). Nguồn ghi `CustomHr` (server quyết).

---

## G. Ranking list CV

### G1. Nghiên cứu lại công thức chấm điểm list CV

**Hiện trạng.** `jobFitScore = 100 × Σ Credit(level) / count`, `Strong=1 / Partial=0.5 / Weak=0`, sắp xếp `ORDER BY overall_match_score DESC` (có `COALESCE` để ứng viên chưa chấm **xuống đáy** — bẫy NULL-đầu của Postgres đã bịt). `verificationRisk` là **cờ đứng cạnh điểm, không nhập vào điểm**.

**Vấn đề.** Công thức hiện tại **đúng về nguyên tắc** (số do code tính từ bằng chứng — đã sửa đúng lỗi "AI phán số mâu thuẫn với chính bằng chứng nó liệt kê") nhưng **thiếu hai chiều quan trọng**: không có must-have (A1), không có trọng số (A3).

**Giải pháp** — xếp hạng **hai tầng**, không nhét mọi thứ vào một con số:

**Tầng 1 — phân loại (không phải điểm):**
| Nhóm | Điều kiện |
|---|---|
| ❌ Không đạt | Thiếu bằng chứng ở **bất kỳ** `MustHave` nào |
| ✅ Đạt | Đủ mọi `MustHave` |

**Tầng 2 — xếp hạng trong nhóm "Đạt":**
```
jobFitScore = 100 × Σ(Credit(level) × weight) / Σ(weight)
```

Giữ **3 nguyên tắc đã chốt**, không được phá khi sửa:
1. **Điểm do code tính**, AI chỉ trích bằng chứng.
2. **`verificationRisk` không nhập vào điểm** — gộp hai thứ khác bản chất vào một số là làm mất khả năng giải thích nó.
3. `Weak` = **việc cần hỏi ở vòng phỏng vấn**, không phải kết luận ứng viên không có.

Đóng dấu `screening_version = 3`. ⚠ **Không backfill hàng loạt** dữ liệu cũ — HR chốt lại `job_needs` rồi rescreen từng campaign (endpoint đã có).

---

## H. Xuyên suốt

### H1. Bớt phần "quăng file" — chỉ giữ chỗ thật sự cần

**Hiện trạng** — 6 điểm nhận file:

| Điểm | Dùng làm gì | Giữ? |
|---|---|---|
| `POST /files` (CV/JD) — Interview | Parse ra text để phân tích | **Giữ** (CV chỉ có dạng file) |
| Audio câu trả lời | Bản ghi buổi thi | **Giữ** (bắt buộc) |
| CV hàng loạt B2B | Sàng lọc | **Giữ** (bắt buộc) |
| Ảnh khuôn mặt | Face verify | **Giữ** (bắt buộc) |
| `JdFile` campaign | JD | **Bỏ** — đã có `jdText`, và C11 đã quy định *text thắng, bỏ file* |
| `CriteriaFile` campaign | Tiêu chí | **Bỏ** — C12 đã chuyển sang nhập có cấu trúc, file không parse được thành `criteria[]` |

**Vấn đề.** `JdFile`/`CriteriaFile` là **đường chết**: gửi lên thì thường bị bỏ qua, không xác thực được nội dung, mà vẫn tốn chỗ lưu và tạo kỳ vọng sai cho người dùng (FE B2B thực tế **chỉ có ô textarea `jdText`**, không có ô upload JD).

**Giải pháp.** Gỡ `JdFile` + `CriteriaFile` khỏi DTO và UI. Giữ cột trong DB (dữ liệu cũ), chỉ ngừng nhận mới.

---

### H2. "Ngoài prompt ra thì phải có tiêu chí"

**Hiện trạng.** Đây là **nguyên tắc kiến trúc đúng nhất** của cả danh sách, và hệ thống **đã áp ở 3 chỗ** (nên có sẵn khuôn mẫu để nhân rộng):
- `jobFitScore` — AI trích bằng chứng, **code tính điểm**.
- Cổng chất lượng bài giảng — AI phải tự khai mỗi mục phục vụ tiêu chí nào, **code kiểm phủ** bằng phép hợp; trượt ⇒ trả lại viết lại.
- Grounding — model chỉ được trích dẫn `chunkId` **trong tập đã cấp** ⇒ chống bịa nguồn *bằng cấu trúc*.

**Chỗ chưa áp:** B1 (điểm CV B2C), B5 (level), E3 (cấu trúc roadmap).

**Giải pháp.** Nâng thành **luật chung** ghi vào `docs/rules.md`:
> Mọi con số dùng để **quyết định** (xếp hạng, pass/fail, level) phải **do code tính** từ dữ kiện AI trích ra. AI **không bao giờ** trả thẳng con số quyết định. Mọi đầu ra AI phải qua **cổng kiểm bằng code** trước khi lưu.

---

### H3. Làm lại UI

**Hiện trạng — những chỗ BE đã có mà FE chưa nối** (đây là danh sách cụ thể để bắt đầu, không phải "làm lại toàn bộ"):

| Chức năng | BE | FE |
|---|---|---|
| Roadmap chọn report (E1) | ✅ | ❌ `grep sessionIds` = rỗng |
| Ứng viên có cờ nhưng chưa chấm (D3) | ✅ `unscoredFlagged` | ❌ chỉ hiện "chưa có ứng viên nào được chấm" |
| Chỉnh sửa `job_needs` trước publish | ✅ | cần rà |
| Tiến độ lesson (E4) | ✅ | ❌ |
| Ô nhập điểm HR override | ✅ | ⚠ **thiếu nhãn `%`** — HR gõ `8` theo thang 10 thành 8% ⇒ Fail oan |

**Giải pháp.** Ưu tiên **nối những thứ BE đã có** trước khi thiết kế lại giao diện — đây là phần cho ra giá trị nhanh nhất và rẻ nhất.

---

### H4. Tài liệu sơ sài

**Hiện trạng.** Có `docs/` khá đầy (rules/architecture/services/decisions) nhưng: bảng task **lệch code ở quy mô lớn**, `progress.md` là nhật ký chạy dài không đọc được để nắm hệ thống, tài liệu API chưa mô tả đủ mã lỗi.

**Giải pháp.**
1. Tách **tài liệu mô tả hệ thống** (đọc để hiểu) khỏi **nhật ký thi công** (`progress.md`).
2. Mỗi service: 1 trang **API + DB + luật nghiệp vụ** cập nhật đúng code.
3. Bổ sung **sơ đồ luồng** cho 4 luồng chính: phân tích CV · phỏng vấn B2C · chiến dịch B2B · roadmap.
4. Đưa các luật vừa chốt ở tài liệu này vào `docs/rules.md` (H2, C1, B5).

---

### H5. Dùng Google AI Studio dựng khung cho chức năng khó

**Nhận xét.** Hợp lý cho **thiết kế prompt và schema đầu ra** — đó đúng là chỗ tốn nhiều vòng thử. Nhưng phải giữ ranh giới:
- **Dùng được**: thử prompt, chốt cấu trúc JSON trả về, so vài mô hình.
- **Không bê thẳng vào**: mọi đầu ra vẫn phải qua **cổng kiểm bằng code** (H2) và **hợp đồng hai đầu** (khai tường minh ở cả `schemas.py` lẫn DTO .NET).
- ⚠ Bài học đã trả giá **3 lần** trong dự án: thêm field mà **quên khai ở một đầu** thì nó **bị nuốt im lặng** — không lỗi, không cảnh báo, chỉ là tính năng không chạy.

---

### H6. Quán triệt lại tất cả các luồng

**Giải pháp.** Với **mỗi luồng**, viết ra một trang gồm 4 phần rồi **đối chiếu code**:
1. **Đầu vào** — ai nhập gì, cái gì bắt buộc.
2. **Trạng thái** — bảng chuyển trạng thái đầy đủ, **kể cả nhánh hỏng** (bỏ ngang, AI lỗi, hết credit).
3. **Đầu ra** — người dùng nhận được gì, xuất ra được gì.
4. **Tiền** — chỗ nào trừ credit, hỏng thì hoàn ở đâu.

4 luồng: **phân tích CV** · **phỏng vấn B2C** · **chiến dịch B2B** · **roadmap**.

---

## Thứ tự đề xuất

**Đợt 1 — sửa cái đang sai** (kết quả hiện đang sai, làm trước)
1. **C2** anti-cheat false-positive *(đã có bản vá, chỉ cần merge + nghiệm thu đối chứng ngược)*
2. **B4 + B5** suy level từ CV *(mở khoá E2, và là gốc của nhiều thứ khác)*
3. **A1 + A2** tiêu chí bắt buộc *(mở khoá A3, G1)*

**Đợt 2 — công thức điểm** *(đụng xếp hạng ⇒ phải đóng dấu phiên bản)*
4. **A3 + G1** trọng số + phân loại 2 tầng cho CV
5. **F2** trọng số câu hỏi + công thức điểm buổi phỏng vấn

**Đợt 3 — nối những thứ BE đã có ra người dùng**
6. **E1, E4, D3, H3** — FE
7. **B2 + B3** đổi cấu trúc CV analysis B2C *(breaking, lockstep 2 repo)*

**Đợt 4 — dọn**
8. **D1** tắt follow-up cho B2B · **D4** bỏ repo, thêm xuất report · **H1** bỏ 2 chỗ upload chết
9. **E3** ép cấu trúc roadmap · **H4/H6** tài liệu + rà luồng

**Việc nền, làm song song:** **D2** dựng gold set — không có nó thì **không đo được** bất kỳ cải thiện nào về chất lượng chấm điểm.

---

## Những chỗ cần team chốt (không tự quyết được)

1. **B5** — ranh giới định lượng 4 mức level (bảng ở B5 là **đề xuất**, chưa hiệu chuẩn bằng dữ liệu).
2. **A3** — thang trọng số cho HR: số tự do hay 3 mức có sẵn (`Critical/High/Normal`)?
3. **D1** — B2C có tắt follow-up không, hay chỉ tắt B2B?
4. **C1** — có làm "chế độ thi thử nghiêm túc" cho B2C không (nếu có thì bật **cả cụm** anti-cheat, không bật lẻ camera)?
5. **F2/G1** — điểm cũ và điểm mới **không so sánh được**: campaign đang chạy dở xử lý thế nào?
