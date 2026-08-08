# Prompt cho phiên sau — dán nguyên khối này

> Soạn cuối phiên 2026-08-08e. Mọi con số dưới đây **đã đo**, nhưng vẫn phải đo lại — doc trôi.

---

Tiếp tục ISAS sau phiên 2026-08-08e.

**ĐỌC TRƯỚC:** `AGENTS.md` · `docs/progress.md` (mục **2026-08-08e**, ngay trước §Pha hiện tại) · `docs/tasks.md` (`SC1` `SC2` `BK34` `RAG1` `RAG2`).
⚠ **ĐỪNG trích số test / trạng thái task trong doc làm mốc — ĐO thật.** pytest: `src/services/Isas.AIService/.venv/bin/python -m pytest src/services/Isas.AIService/tests -q`

## Trạng thái đầu phiên

- BE `origin/main` = `502c73e` · **deploy đang chạy `455a61c`** (chênh là docs-only, hành vi y hệt — verify bằng label `org.opencontainers.image.revision`, KHÔNG bằng dấu thời gian)
- 🔴 **`origin/feat/scoped-criteria-scoring` đã push, CHƯA merge** — 6 commit, **1 migration `AddScoringScopeAndQuestionTargets` CHƯA apply DB thật**
- Gate trên nhánh đó: build 0 error · .NET **1976** (Shared 30 · Auth 189 · Campaign 577 · Interview 665 · Payment 515) · pytest **533** · `has-pending` No changes ×4
- FE `master` = `f068ef8`, đã deploy Vercel. ⚠ FE remote **không có `main`** — nhánh chính là `master`
- 4 DB **đã đồng bộ** (`SCHEMA_GATE_MODE=enforce python3 scripts/check-schema-gate.py` → PASS)

## Việc 1 (ưu tiên) — merge + deploy nhánh chấm-theo-phạm-vi

🔴 **Migration phải đi TRƯỚC hoặc CÙNG LÚC deploy, không được đi sau** — đã lặp 2 lần (02/08 và 05/08): CI deploy ngay khi merge, image mới đọc cột chưa tồn tại → `42703` trên đường request thật, mà `/health` vẫn xanh nên không ai biết trong nhiều giờ.

Sau deploy, verify bằng **hành vi** (không phải vắng lỗi): tạo buổi B2C → xem `practice_questions.target_criterion_ids` có được điền · trả lời → xem `answer_scores` chỉ có 4 tiêu chí cách nói + tiêu chí được nhắm, **không phải 7** · `practice_sessions.scoring_scope_version = 2`.

## Việc 2 — `SC1`: câu gốc phải phủ đủ 3 tiêu chí nội dung

Đây là **vế còn thiếu của quyết định user đã chốt**; thiếu nó thì điểm thành *"may mắn được hỏi trúng tủ"*.

⚠ **Không phải chỉ đổi env.** `BUS-01` chia ngân sách `seeds = clamp(ceil(MaxQuestions/(1+maxDeep)), 1, SeedCount)`, mà **FE luôn gửi `questionCount` đè `MaxQuestions`** (mặc định 5). Đặt `SeedCount=3` mà `questionCount` nhỏ thì vẫn ra 1–2 câu gốc. Phải tính lại cả cụm `SeedCount` / `MaxDeepPerQuestion` / mặc định `questionCount` phía FE **rồi mới đổi env**.
Prod hiện: `Adaptive__Enabled=true` · `SeedCount=1` · `MaxQuestions=6` · `MaxFollowUps=3` (env compose đè appsettings — sửa phải đủ cả cụm).

## Việc 3 — sau khi deploy: reindex 25 nguồn RAG

Hai fix RAG (nhãn trích dẫn · điểm uy tín) **chỉ áp cho chunk mới**. 25 nguồn hiện có vẫn mang nhãn cũ ("Help improve MDN") và `reputation = null` cho tới khi gọi `POST /api/v1/interview/admin/knowledge/{id}/reindex`.
Verify sau reindex: nguồn Context7 phải có `reputation` khác null; nhãn trích dẫn phải **bắt đầu bằng tên nguồn**.

## Việc 4 — chọn 1 trong 3, hỏi user trước

- **`RAG1`** soạn `rubric_levels` (E9 đang trơ: descriptor là `"Mức 3/5"` = vô nghĩa; mô tả tiêu chí **nội dung** chỉ 51–73 ký tự trong khi tiêu chí **cách nói** 292–409). Đây là gốc của "chấm không ổn định".
- **`RAG2`** bật `SelfConsistencyN` có chọn lọc để **đo** dao động điểm. ⚠ Phép đo rẻ nhất trước khi làm `RAG1`: **chấm cùng một bài nhiều lần rồi so** — nếu "Chiều sâu kỹ thuật" nhảy 3→5 thì đó là bằng chứng cứng. Chi phí: token ×N và `answer_scores` ×N.
- **`BK34`** nạp RAG: chia batch embed + bỏ qua/cắt chunk quá cỡ + .NET giữ nguyên văn `detail` lỗi.

## ⚠ Ba thứ CHẠY THẬT trên prod nhưng KHÔNG có trong git
Dựng lại server là mất, phải làm tay:
1. `questions.guidance` version 1 (câu hỏi ngắn 17–20 từ). Revert: `DELETE /api/v1/interview/admin/prompts/questions.guidance`
2. `GITHUB_TOKEN` trong `~/docker/main/.env` — **fine-grained PAT có HẠN** (mặc định 30 ngày). Hết hạn thì BC18 trả **400** kèm `GitHub API trả 401`, **KHÔNG rơi về ẩn danh**; ✅ không mất credit (`catch { ReleaseAsync; throw }`)
3. **Corpus RAG 25 nguồn / 682 chunk** (BA 399 · FE 146 · BE 137)

## Bẫy đo lường — đã cắn trong phiên trước, đừng lặp
- Đọc kết quả buổi luyện: **`result.overallScore`**, KHÔNG phải cấp 1. Tiêu chí là **`name`**, KHÔNG phải `criterionName`
- Prefix gateway nhóm practice: **`/api/v1/interview/practice/...`**. Gateway ở cổng **5050**
- Đơn hàng: **`/payment/order/my-orders`**, không phải `/payment/my-orders`
- Container: **`rabbitmq-main`**, **`aiapi-main`** (không phải `rabbitmq`/`aiapi`)
- Session vừa `Scored` thì BC9 tính **sau đó vài giây** — đọc ngay là chạy đua với chính phép tính
- Đếm migration bằng `ls | grep -v Snapshot` là **SAI** (3 migration có chữ "Snapshot" trong tên) — dùng `scripts/check-schema-gate.py`
- `ssh host 'read -rsp ...'` thiếu `-t` ⇒ không có TTY ⇒ biến rỗng mà lệnh sau **vẫn chạy** = trông như đã xong. `read -p` là cú pháp **bash**, user dùng **zsh**
- **Luôn kèm một endpoint đã biết là sống** làm đối chứng; đối chứng hỏng thì phép đo hỏng, không phải hệ thống hỏng

## Cách làm (user đã chốt)
Chia nhiều agent chạy song song **theo SỞ HỮU FILE** (không theo task), worktree riêng, **pin cùng một base SHA**, cấp **đường dẫn harness mutation riêng cho từng agent** (vòng trước 2 agent ghi đè `mutate.py` của nhau). 1 đợt = 1 branch → 1 PR.
**Mutation check bắt buộc** cho đường tiền/điểm số: harness phải tự chứng minh đang chạy đúng thư mục, so `shasum` (KHÔNG `git diff` — file untracked thì im lặng), `os.utime` ép mtime khi restore, chạy lại baseline sau mỗi phép. **Mutation ra XANH thì ĐIỀU TRA, đừng nhận là "phòng thủ dư".**

## Còn treo từ trước (không chặn)
`Q1`/`OPS6` (prod chạy `Development` — lần thứ 6) · `BK31` · `R3` `R6` `R8`–`R14` · `Q12` nửa nghiệp vụ · `BC17`
