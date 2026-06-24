# ISAS — Business Rules

Tập hợp quy tắc nghiệp vụ + state machine của hệ thống. Tham chiếu code trong InterviewService (`PracticeService`, `AnswerService`, `StuckAnswerRepublisher`) và AIService worker.

---

## 1. State machine — Session

```
GeneratingQuestions ──► Ready ──► InProgress ──► Scoring ──► Scored
        │                                            
        └──► Failed (sinh câu hỏi lỗi)
```

| Trạng thái | Ý nghĩa | Chuyển tiếp |
|---|---|---|
| `GeneratingQuestions` | Đang gọi AI sinh câu hỏi | → Ready (xong) / Failed (lỗi) |
| `Ready` | Có câu hỏi, chưa trả lời câu nào | → InProgress (upload answer đầu) |
| `InProgress` | Đang trả lời | → Scoring (submit) |
| `Scoring` | Đã chốt, chờ chấm nốt | → Scored (mọi answer xong) |
| `Scored` | Hoàn tất, có điểm | (kết thúc) |
| `Failed` | Sinh câu hỏi thất bại | (kết thúc) |

> `Completed` tồn tại trong enum nhưng **không dùng** trong luồng hiện tại.

**Quy tắc chuyển:**
- Chỉ `Ready`/`InProgress` mới được **submit**. Phải có ≥1 answer mới submit được.
- Submit set `Scoring` + `CompletedAt`. Nếu mọi answer đã xong ngay lúc submit (chấm dần đã chấm trước) → đóng thẳng `Scored`.
- Session chỉ đóng sang `Scored` khi đang `Scoring` **và** mọi answer ∈ {Scored, Skipped, Failed}.

---

## 2. State machine — Answer

```
Uploaded ──(publish OK)──► Scoring ──(callback result)──► Scored
   │                          │
   │ (publish hụt)            └──(callback failed)──► Failed
   └── giữ Uploaded → republisher đẩy lại
```

| Trạng thái | Ý nghĩa |
|---|---|
| `Uploaded` | Đã có audio, publish hụt (chưa lên queue) |
| `Scoring` | Đã publish job, đang/chờ chấm |
| `Scored` | Đã có điểm |
| `Failed` | Lỗi chấm vĩnh viễn (audio hỏng / LLM output sai) |
| `Skipped` | (dự phòng — câu bỏ qua) |

> `Transcribing`/`Transcribed` có trong enum nhưng không dùng.

---

## 3. Upload câu trả lời

- **Tối đa 1 answer mỗi câu hỏi** (unique `session_id + question_id`). Upload lại = ghi đè (idempotent: `fileId = answerId`, cùng object key).
- Upload lại reset `transcript = null`, `status = Uploaded`, rồi publish lại.
- Câu trả lời đầu tiên: session `Ready` → `InProgress`.
- Không cho upload khi session đã `Completed`/`Scoring`/`Scored`.
- Chỉ chủ session (`candidateId` khớp) được upload.

---

## 4. Chấm điểm dần (incremental scoring)

- Publish job chấm **ngay khi upload** (không đợi submit) → trải tải, kết quả về sớm.
- Submit **không** publish lại (tránh chấm trùng) — chỉ chốt sổ.
- Publish lỗi **không** làm hỏng upload: answer vẫn lưu, để republisher xử lý sau.
- Mỗi job kèm **rubric active** của `JobCategory` + `RubricVersion`. Không có rubric active → bỏ qua publish (log warning).

---

## 5. Phân loại lỗi worker (tạm thời vs vĩnh viễn)

| Loại | Ví dụ | Xử lý |
|---|---|---|
| **Tạm thời** | S3 tải lỗi, Gemini rate limit/5xx, callback mạng lỗi | `nack` → republisher đẩy lại |
| **Vĩnh viễn** | transcribe lỗi / transcript rỗng, LLM output không hợp lệ (`ValueError`) | callback `/failed` → answer `Failed` |

> Scoring dùng `temperature=0` (tất định) → lỗi LLM output tái lập → retry vô ích → coi là vĩnh viễn.

---

## 6. Republish answer kẹt (`StuckAnswerRepublisher`)

Quét mỗi **2 phút**, chỉ xét session `InProgress`/`Scoring` và answer có audio:

| Điều kiện | Coi là | Ngưỡng |
|---|---|---|
| `Uploaded` + `last_scoring_published_at = null` + quá grace | publish hụt | **2 phút** (`CreatedAt`) |
| đã publish (`Scoring`) nhưng quá lâu không callback | worker mất tích | **15 phút** (`last_scoring_published_at`) |

- Sau khi đẩy lại thành công: set `Scoring` + dời `last_scoring_published_at = now` → không bị nhặt lại trong 15' kế.
- Answer `Failed`/`Scored` **không** bị nhặt → không republish vô tận.

---

## 7. Idempotency callback

- **result**: xoá điểm cũ cùng `(attemptNo, rubricVersion)` rồi ghi lại → worker retry không nhân đôi điểm.
- **failed**: nếu answer đã `Scored` (callback đến muộn) → bỏ qua, **không** hạ xuống `Failed`.
- Sau khi lưu điểm/đánh dấu Failed → thử đóng session (nếu đang `Scoring` và mọi answer xong).

---

## 8. Rubric & điểm

- Rubric theo `JobCategory`, có `version` + `is_active`. Tiêu chí active của 1 nghề dùng **chung 1 version**.
- Worker chấm phải đủ **mọi** tiêu chí; thiếu → lỗi (vĩnh viễn). Điểm bị **kẹp** trong `[0, maxScore]`.
- Bỏ qua tiêu chí Gemini bịa (criterionId không có trong rubric); chống trùng tiêu chí.
- `answer_scores` gắn `rubric_version` lúc chấm → sửa rubric không làm loạn điểm cũ.
- Khi hiển thị: mỗi tiêu chí lấy **attempt mới nhất** (mở đường self-consistency nhiều attempt sau này).

---

## 9. Sinh câu hỏi (ưu tiên nội dung)

Thứ tự định hướng: **JD > CV > JobCategory**.
- Có JD: JD dẫn nội dung, vẫn neo về vị trí. Có thêm CV → cá nhân hóa.
- Chỉ CV: bám CV trong phạm vi vị trí.
- Không CV/JD: câu hỏi tổng quát theo `JobCategory` (`BA`/`BE`/`FE`).

CV/JD **optional** — không có vẫn luyện được. Số câu hỏi theo `QUESTION_COUNT` (mặc định 5).

---

## 10. Xác thực

- Endpoint người dùng: **JWT Bearer** (AuthService phát). InterviewService validate bằng **cùng** `Jwt:Key`/`Issuer`/`Audience`.
- Callback nội bộ (`/internal/...`): `AllowAnonymous` + header **`X-Internal-Token`** khớp `Internal:Token`. Token này phải giống nhau giữa InterviewService và AIService.
- AIService **không** ghi DB — mọi thay đổi đi qua callback về InterviewService.
