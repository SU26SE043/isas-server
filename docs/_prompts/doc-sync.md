# Prompt mẫu — Đồng bộ TOÀN BỘ doc theo trạng thái thật (chống sót)

> Dùng khi vừa merge/đổi gì đó (feature, service, schema) và cần kéo doc về đúng thực tế.
> Thiết kế để **không sót file**: ép liệt kê inventory động (`find`) + đối chiếu git TRƯỚC khi sửa, và **lập plan, dừng chờ duyệt** theo quy trình ISAS.
> Copy nguyên khối dưới, điền `<…>`, đưa cho agent.

```
Task: Đồng bộ TOÀN BỘ doc theo trạng thái thật của repo (sau khi: <điền việc vừa đổi, vd "merge E1 / thêm service X / đổi schema Y">).

Đọc trước, KHÔNG lệch: AGENTS.md · docs/architecture.md (§2,§2.1,§5,§6,§7,§8) · docs/work-division.md (§1,§2,§7,§8) · decisions.md (D…) — nắm cách các doc liên kết & quy ước D14 (doc = source of truth, không mirror code dở).

⛔ LẬP PLAN, DỪNG, CHỜ DUYỆT (chưa sửa). Plan phải gồm:

1) SỰ THẬT (xác minh từ git/tree, KHÔNG đoán):
   - `git log main --oneline --merges -15` → PR nào đã merge.
   - `git branch -a` → nhánh nào còn dở.
   - `ls src/services/` + `ls **/*.Tests` → service/test project thật trong tree.
   - grep CI (.github/workflows) + gateway appsettings → service nào build/route.

2) INVENTORY DOC (chống sót — liệt kê ĐỘNG, không nhớ tay):
   - `find . -name '*.md' -not -path '*/obj/*' -not -path '*/bin/*'` → liệt kê MỌI file .md.
   - Lập bảng checklist: mỗi file .md = 1 dòng [cần sửa? / lý do / không-đổi-vì].
     Bắt buộc phủ: AGENTS.md, README*, DEPLOYMENT.md, docs/{architecture,work-division,decisions,progress,tasks}.md, docs/services/*.md, và mọi .md khác find ra.
   - File "không đổi" cũng PHẢI ghi vào checklist (ghi rõ "đã current") — không bỏ trống.

3) PER-FILE: liệt kê chính xác chỗ lệch sẽ sửa (dòng/section), giá trị cũ → mới.

4) RÀNG BUỘC:
   - Chỉ sửa status/sự thật (đã merge/đang nhánh/đã có/chưa có) + mâu thuẫn nội tại; KHÔNG redesign, KHÔNG đổi quyết định đã chốt (lệch thì HỎI).
   - Trạng thái task: chỉ passing/merged khi có bằng chứng (PR#/commit); chưa verify e2e thì ghi "⚠ chờ …", không nâng khống.
   - Cross-ref nhất quán giữa các doc (tasks ↔ progress ↔ architecture ↔ service docs): thuật ngữ, PR#, trạng thái.

5) XÁC MINH (sau sửa):
   - `grep -rniE "chờ PR|chưa có folder|🟡 branch|stub|TODO|chờ A[0-9]|PR #<số cũ>"` docs/ AGENTS.md → 0 leftover lệch (mỗi hit còn lại phải giải thích được là đúng thực tế).
   - Đọc lại checklist §2: mọi dòng đã tick (sửa hoặc xác nhận current).
   - Doc-only → build/test không đổi; nếu có đụng code thì `dotnet build` + `dotnet test`.

▶ Sau duyệt: làm đúng plan. Commit nguyên tử trên nhánh riêng (vd `docs/sync-current-state`); message: làm gì + vì sao + PR# đối chiếu. KHÔNG push/PR nếu chưa được yêu cầu.
```

## Mấu chốt chống sót
- **Bước 2** là chìa khoá: ép `find` ra mọi `.md` rồi bắt khai báo TỪNG file (kể cả "không đổi") vào checklist — không cho nhớ-bằng-đầu.
- **Bước 5** là lớp chặn thứ hai: grep marker lệch + đọc lại checklist trước khi commit.
