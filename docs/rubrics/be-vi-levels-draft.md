# J7 — Bản nháp mốc điểm (rubric levels): Backend · Tiếng Việt

> **Đây là bản NHÁP để admin đọc và sửa, KHÔNG PHẢI bản cuối.** File này chỉ nằm trong repo —
> chưa có dòng nào trong `rubric_levels` trên production. Việc lưu thật đi qua
> `POST /admin/rubrics/.../levels/suggest` → admin duyệt/sửa nội dung → `PUT`, do người có quyền
> truy cập hệ thống chạy thật thực hiện, **không phải việc của agent thực thi J1–J8**.

## Vì sao cần

`rubric_levels` hiện có **0 dòng** trên production. Mọi tiêu chí rơi về dải mặc định — với thang
0–5 (mặc định B2C, `B2CRubricSeed.DefaultMaxScore`), điều đó nghĩa là "Mức 0/5 … Mức 5/5" không
có mô tả gì để mô hình đối chiếu khi chọn mức (E9). Đây là nghi can chính của việc chấm không tái
lập được, và là gốc gián tiếp của ca thật đã đo được (câu hỏi hẹp bị chấm trên toàn bộ 7 tiêu chí,
hai tiêu chí không được hỏi tới nhận điểm 0 với lý do "không đề cập" — xem J1).

## Phạm vi bản nháp này

- **Nghề:** Backend (`JobCategory.BE`)
- **Ngôn ngữ:** Tiếng Việt (`vi`)
- **7 tiêu chí** (đúng thứ tự + tên trong `B2CRubricSeed`, KHÔNG được đổi tên — tên là khoá gom
  nhóm của BC12/BC15/F14, đổi tên cắt đứt chuỗi thời gian tiến bộ của mọi người dùng):
  1. Chiều sâu kỹ thuật (`WhenTargeted`)
  2. Thiết kế hệ thống & CSDL (`WhenTargeted`)
  3. Giải quyết vấn đề & thuật toán (`WhenTargeted`)
  4. Giao tiếp & trình bày (`Always`)
  5. Ngữ pháp & dùng từ (`Always`)
  6. Thuật ngữ chuyên ngành (`Always`)
  7. Độ trôi chảy & tự tin (`Always`)
- **Thang điểm:** 0–5 (`B2CRubricSeed.DefaultMaxScore = 5`) → **6 mức** mỗi tiêu chí (0,1,2,3,4,5).

## Quy ước viết mốc (khớp `Isas.Shared.Rubric.CriterionLevelRules` + mẫu E9b)

- Mỗi mốc **bắt buộc** có hai vế viết liền trong MỘT chuỗi: `"CÓ: <quan sát được ở mức này> | CÒN
  THIẾU: <thứ mà mốc CAO HƠN LIỀN KỀ có mà mốc này chưa có>"`. Mốc cao nhất ghi `CÒN THIẾU: —` vì
  không còn mốc nào cao hơn.
- **Cấm dùng tính từ đánh giá** ("tốt", "khá", "chưa đạt", "xuất sắc", "yếu", "ổn") — đó là đổi
  tên con số chứ không định nghĩa gì. Mỗi vế mô tả ứng viên **THỰC SỰ làm/nói được gì**: nêu được
  khái niệm nào, có ví dụ cụ thể hay không, có phân tích đánh đổi hay không, có xét ca biên hay
  không.
- **Đơn điệu:** mốc n+1 thêm ÍT NHẤT MỘT yêu cầu quan sát được so với mốc n, không chồng lấn.
- **Mốc 0** = không có bằng chứng nào cho tiêu chí này (gồm cả câu trả lời trống/lạc đề/chỉ nhắc
  lại câu hỏi) — không mô tả mốc 0 như "có nhưng sơ sài".
- Độ dài mỗi descriptor (cả hai vế gộp lại) phải trong khoảng **20–500 ký tự** — mọi dòng dưới
  đây đã nằm trong khoảng đó.

---

## 1. Chiều sâu kỹ thuật

| Mức | Descriptor |
|---|---|
| 0 | CÓ: không nêu được khái niệm kỹ thuật nào liên quan | CÒN THIẾU: mọi thứ — câu trả lời trống hoặc lạc đề hoàn toàn. |
| 1 | CÓ: gọi đúng tên một khái niệm/công nghệ liên quan nhưng không giải thích được cách nó hoạt động | CÒN THIẾU: cơ chế hoạt động, ví dụ cụ thể, và mọi đánh đổi kỹ thuật. |
| 2 | CÓ: giải thích đúng cơ chế hoạt động cơ bản của MỘT khái niệm | CÒN THIẾU: liên hệ với ngữ cảnh thực tế của câu hỏi và so sánh với phương án khác. |
| 3 | CÓ: giải thích cơ chế VÀ đưa ra một ví dụ áp dụng thực tế phù hợp câu hỏi | CÒN THIẾU: phân tích đánh đổi (trade-off) giữa các lựa chọn kỹ thuật. |
| 4 | CÓ: giải thích cơ chế, ví dụ thực tế, VÀ nêu được ít nhất một đánh đổi kỹ thuật (hiệu năng/độ phức tạp/chi phí) | CÒN THIẾU: xử lý ca biên hoặc giới hạn của giải pháp đã nêu. |
| 5 | CÓ: đầy đủ như mức 4 VÀ chỉ ra ít nhất một ca biên hoặc giới hạn cùng cách xử lý | CÒN THIẾU: — |

## 2. Thiết kế hệ thống & CSDL

| Mức | Descriptor |
|---|---|
| 0 | CÓ: không đề cập mô hình dữ liệu hay kiến trúc nào | CÒN THIẾU: mọi thành phần của một thiết kế hệ thống. |
| 1 | CÓ: nêu tên một thành phần kiến trúc hoặc bảng dữ liệu liên quan | CÒN THIẾU: quan hệ giữa các thành phần và lý do chọn chúng. |
| 2 | CÓ: mô tả được quan hệ giữa các thành phần/bảng dữ liệu chính | CÒN THIẾU: cân nhắc về khả năng mở rộng (scale) hoặc độ tin cậy. |
| 3 | CÓ: mô tả quan hệ giữa các thành phần VÀ nêu một cân nhắc về khả năng mở rộng hoặc độ tin cậy | CÒN THIẾU: so sánh với phương án thiết kế thay thế. |
| 4 | CÓ: mô tả thiết kế, cân nhắc mở rộng/tin cậy, VÀ so sánh với ít nhất một phương án thay thế kèm lý do chọn | CÒN THIẾU: xử lý tình huống lỗi hoặc đảm bảo tính nhất quán dữ liệu ở quy mô lớn. |
| 5 | CÓ: đầy đủ như mức 4 VÀ nêu được cách xử lý lỗi hoặc đảm bảo tính nhất quán dữ liệu khi hệ thống mở rộng | CÒN THIẾU: — |

## 3. Giải quyết vấn đề & thuật toán

| Mức | Descriptor |
|---|---|
| 0 | CÓ: không đưa ra hướng giải quyết nào | CÒN THIẾU: mọi bước phân tích vấn đề. |
| 1 | CÓ: nêu được vấn đề cần giải quyết bằng lời của chính mình | CÒN THIẾU: một hướng giải quyết cụ thể. |
| 2 | CÓ: đề xuất MỘT hướng giải quyết cụ thể cho vấn đề | CÒN THIẾU: giải thích vì sao hướng đó phù hợp hoặc phân tích độ phức tạp. |
| 3 | CÓ: đề xuất hướng giải quyết VÀ giải thích được vì sao nó phù hợp với bài toán | CÒN THIẾU: phân tích độ phức tạp (thời gian/không gian) hoặc xét ca biên. |
| 4 | CÓ: đề xuất, giải thích, VÀ phân tích độ phức tạp hoặc xét ít nhất một ca biên | CÒN THIẾU: so sánh với phương án thay thế để chọn giải pháp tối ưu hơn. |
| 5 | CÓ: đầy đủ như mức 4 VÀ so sánh được với ít nhất một phương án thay thế, nêu lý do phương án đã chọn tốt hơn | CÒN THIẾU: — |

## 4. Giao tiếp & trình bày

| Mức | Descriptor |
|---|---|
| 0 | CÓ: không trình bày được ý nào mạch lạc | CÒN THIẾU: mọi cấu trúc trình bày. |
| 1 | CÓ: nói được một vài ý rời rạc liên quan tới câu hỏi | CÒN THIẾU: trình tự logic nối các ý với nhau. |
| 2 | CÓ: trình bày các ý theo một trình tự có thể theo dõi được | CÒN THIẾU: dẫn dắt rõ ràng giữa các phần (mở đầu — nội dung chính — kết luận). |
| 3 | CÓ: trình bày có trình tự VÀ có dẫn dắt rõ giữa các phần | CÒN THIẾU: nhấn mạnh đúng trọng tâm câu hỏi thay vì liệt kê dàn trải. |
| 4 | CÓ: trình bày có cấu trúc, dẫn dắt rõ, VÀ nhấn đúng trọng tâm câu hỏi | CÒN THIẾU: điều chỉnh mức độ chi tiết phù hợp người nghe (không quá kỹ thuật hoặc quá sơ sài). |
| 5 | CÓ: đầy đủ như mức 4 VÀ điều chỉnh được mức độ chi tiết phù hợp với ngữ cảnh câu hỏi | CÒN THIẾU: — |

## 5. Ngữ pháp & dùng từ

> Nhắc lại luật F12 (đã áp trong prompt chấm, không cần lặp ở đây): transcript là sản phẩm ASR
> (Whisper) — KHÔNG xét chính tả/dấu câu/viết hoa, chỉ xét chọn từ, cấu trúc câu, từ đệm/lặp thừa.

| Mức | Descriptor |
|---|---|
| 0 | CÓ: câu nói không đủ ý để hiểu được nội dung | CÒN THIẾU: mọi cấu trúc câu hoàn chỉnh. |
| 1 | CÓ: một vài câu đủ chủ-vị nhưng phần lớn câu cụt hoặc dài lê thê không dứt ý | CÒN THIẾU: dùng từ chính xác theo đúng nghĩa. |
| 2 | CÓ: câu đủ ý, dùng từ đúng nghĩa ở phần lớn thời gian | CÒN THIẾU: hạn chế từ đệm/lặp thừa ("ờ", "kiểu như") gây khó hiểu. |
| 3 | CÓ: câu đủ ý, dùng từ đúng nghĩa, VÀ ít từ đệm/lặp thừa | CÒN THIẾU: chuyển ý mượt giữa các câu liên tiếp. |
| 4 | CÓ: câu gọn, dùng từ chính xác, ít từ đệm, VÀ chuyển ý mượt giữa phần lớn các câu | CÒN THIẾU: duy trì chất lượng đó xuyên suốt toàn bộ câu trả lời dài. |
| 5 | CÓ: đầy đủ như mức 4 VÀ duy trì được xuyên suốt câu trả lời, kể cả đoạn dài hoặc phức tạp | CÒN THIẾU: — |

## 6. Thuật ngữ chuyên ngành

> Ví dụ thuật ngữ backend (khớp `TerminologyDesc` trong `B2CRubricSeed`): transaction, index,
> deadlock, idempotent, cache, race condition, ACID.

| Mức | Descriptor |
|---|---|
| 0 | CÓ: không dùng thuật ngữ chuyên ngành nào | CÒN THIẾU: mọi thuật ngữ liên quan tới câu hỏi. |
| 1 | CÓ: nhắc tên một thuật ngữ chuyên ngành liên quan (vd transaction, cache, index) | CÒN THIẾU: dùng đúng ngữ cảnh hoặc giải thích được ý nghĩa. |
| 2 | CÓ: dùng thuật ngữ ĐÚNG ngữ cảnh của câu hỏi | CÒN THIẾU: giải thích được ý nghĩa của thuật ngữ khi được hỏi lại. |
| 3 | CÓ: dùng đúng ngữ cảnh VÀ giải thích được ý nghĩa của ít nhất một thuật ngữ đã dùng | CÒN THIẾU: dùng nhất quán nhiều thuật ngữ liên quan xuyên suốt câu trả lời. |
| 4 | CÓ: dùng đúng ngữ cảnh, giải thích được, VÀ dùng nhất quán từ 2 thuật ngữ liên quan trở lên | CÒN THIẾU: phân biệt được các thuật ngữ dễ nhầm lẫn (vd deadlock vs race condition). |
| 5 | CÓ: đầy đủ như mức 4 VÀ phân biệt rõ được các thuật ngữ dễ nhầm lẫn với nhau | CÒN THIẾU: — |

## 7. Độ trôi chảy & tự tin

> Tiêu chí F11 — chấm bằng SỐ ĐO thật (tốc độ nói/khoảng lặng/từ đệm), không phải suy diễn từ nội
> dung câu trả lời. Mô tả dưới đây cố ý bám các đại lượng đo được (nhịp, khoảng dừng, từ đệm),
> không nhắc con số ngưỡng cụ thể — ngưỡng nằm trong `build_delivery_block`, để hai chỗ khỏi lệch
> nhau khi tinh chỉnh sau này.

| Mức | Descriptor |
|---|---|
| 0 | CÓ: gần như không nói được gì liên tục — im lặng kéo dài hoặc bỏ giữa chừng | CÒN THIẾU: mọi đoạn nói liền mạch. |
| 1 | CÓ: nói được từng đoạn ngắn xen kẽ nhiều khoảng dừng dài | CÒN THIẾU: nhịp nói đều và liên tục hơn giữa các đoạn. |
| 2 | CÓ: nói liên tục được các đoạn vừa, thỉnh thoảng dừng lâu giữa câu | CÒN THIẾU: giảm số lần dừng lâu và giảm từ đệm dày đặc. |
| 3 | CÓ: nhịp nói tương đối đều, số lần dừng lâu giữa câu ở mức thấp | CÒN THIẾU: hạn chế thêm từ đệm ("ừm", "ờ") và tránh lặp lại đầu câu. |
| 4 | CÓ: nhịp nói đều, hiếm dừng lâu, ít từ đệm, không lặp lại đầu câu nhiều lần | CÒN THIẾU: duy trì được nhịp đó xuyên suốt toàn bộ câu trả lời dài. |
| 5 | CÓ: đầy đủ như mức 4 VÀ duy trì nhịp nói đều xuyên suốt toàn bộ câu trả lời | CÒN THIẾU: — |

---

## Hệ quả của việc LƯU (đọc trước khi bấm Lưu)

Đường lưu thật là `AdminB2CRubricService.AppendVersionAsync` (`src/services/Isas.InterviewService/Services/AdminB2CRubricService.cs:212`), và nó **append-only**:

- Bấm Lưu **hạ cờ `IsActive`** của toàn bộ bộ 7 tiêu chí BE/vi đang hiệu lực, rồi **tạo một bộ
  MỚI** với `version = version_hiện_tại + 1`.
- Bộ mới nhận **ID tiêu chí MỚI** (mint lại, không tái dùng ID cũ) — đây là lý do carry-over khi
  sửa qua `PUT campaign/{id}` (CAMP-16) ghép theo **tên**, không theo ID.
- **Buổi đã chấm giữ nguyên bộ CŨ** (`practice_sessions.b2c_rubric_version` ghim tại lúc tạo buổi)
  — sửa mốc không hồi tố, không đổi điểm của ai đã thi.
- **Dữ liệu cũ nguyên vẹn**, không mất, không phải backfill.
- Benchmark cộng đồng (F14) và biểu đồ tiến bộ (BC15) gom theo **TÊN** tiêu chí (`"Chiều sâu kỹ
  thuật"`, v.v.), KHÔNG theo ID — nên việc tăng version **không** cắt đứt chuỗi thời gian so sánh,
  miễn là **tên tiêu chí giữ nguyên** (đúng lý do bản nháp này không đổi tên nào ở trên).
- Rubric riêng của candidate (BC16) là bộ TÁCH BIỆT — lưu bộ chuẩn BE/vi ở đây **không** ảnh
  hưởng người dùng đã tự tạo rubric riêng cho họ.

## Những gì KHÔNG được làm với file này

- **KHÔNG** viết SQL chèn thẳng vào `rubric_levels` — mọi thay đổi phải qua API admin để đi đúng
  đường append-only + audit ở trên.
- **KHÔNG** sửa `B2CRubricSeed` để nhét mốc vào — seed chỉ chạy lúc cài đặt MỚI (`HasData`,
  Npgsql-only); production đã có dữ liệu và sẽ không nhận thêm dòng từ đó.
- File này **không tự động load vào hệ thống** — nó là bản thảo cho admin đọc, sao chép nội dung
  (có sửa lại nếu cần) vào form `POST .../levels/suggest` → duyệt → `PUT`.
