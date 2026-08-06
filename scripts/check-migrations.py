#!/usr/bin/env python3
"""Soi migration EF Core trước khi merge: raw SQL thiếu `;` và thao tác phá huỷ.

Vì sao cần (hai bài học đã trả giá bằng sự cố thật):

  1) `AddAuditColumnsAndTypes` (DB14/Payment) có `migrationBuilder.Sql()` với
     `ALTER … USING (CASE … END)` **thiếu `;` cuối**. `dotnet ef database update`
     vẫn chạy được nên test + L3 KHÔNG bắt; nhưng `dotnet ef migrations script
     --idempotent` nối câu đó vào khối `DO $EF$ … END IF` ⇒ vỡ cú pháp ĐÚNG LÚC
     apply lên production. Đây là lỗi chỉ lộ ra ở bước deploy — cửa duy nhất
     chặn được nó là đọc source, nên guard này chạy trên PR.

  2) `DropColumn`/`RenameColumn` trong `Up()` là hợp lệ, nhưng khi image cũ còn
     map cột vừa drop thì mọi request đọc bảng đó trả 500 (`42703 column does
     not exist` — đã xảy ra 02/08 và 05/08). Không thể tự động phân biệt "cố ý"
     với "quên", nên đây là **CẢNH BÁO** để người review xác nhận, KHÔNG chặn.

Cách phân biệt (quan trọng — đừng rút gọn):
  - thiếu `;` cuối câu SQL   -> vỡ idempotent script lúc deploy => LỖI, exit 1
  - thao tác phá huỷ trong Up -> cần deploy đúng thứ tự         => CẢNH BÁO, exit 0
  - `Sql()` trong `Down()`    -> không nằm trên đường apply tới  => BỎ QUA
  - đối số `Sql()` không phải literal (biến/hằng) -> không kết luận được => BỎ QUA

Chỉ đọc file, không chạm DB, không cần secret.

    python3 scripts/check-migrations.py

Lệch quy ước có chủ đích: khi chạy trong GitHub Actions (`GITHUB_ACTIONS=true`)
script in thêm `::error file=…,line=…::` / `::warning …`. Annotation là thứ DUY
NHẤT gắn được cảnh báo vào đúng dòng trong tab Files changed của PR, và `ci.yml`
đã dùng `::error::` sẵn (step "Verify AIService lock") nên đây là mở rộng quy
ước có sẵn chứ không phải phát minh mới. Ngoài CI thì output vẫn thuần stdout.
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# Scope CỨNG vào 4 thư mục Migrations thật. KHÔNG `rglob` từ gốc repo:
# `.claude/worktrees/` chứa các bản sao TOÀN BỘ repo (mỗi agent một worktree),
# mỗi bản đủ 4 thư mục Migrations ⇒ rglob sẽ đếm gấp nhiều lần và báo trùng.
MIGRATION_DIRS = [
    f"src/services/Isas.{svc}Service/Migrations"
    for svc in ("Auth", "Interview", "Campaign", "Payment")
]

# `AlterColumn` LUÔN ở dạng generic: `migrationBuilder.AlterColumn<long>(`.
# Thiếu nhánh `(?:<…>)?` là bỏ sót 100% AlterColumn mà script vẫn báo xanh.
DESTRUCTIVE_OPS = (
    "DropColumn",
    "DropTable",
    "RenameColumn",
    "RenameTable",
    "AlterColumn",
    "DropPrimaryKey",
    "DropUniqueConstraint",
)
RE_DESTRUCTIVE = re.compile(
    r"\bmigrationBuilder\s*\.\s*(" + "|".join(DESTRUCTIVE_OPS) + r")\s*(?:<[^>()]*>)?\s*\("
)
RE_SQL_CALL = re.compile(r"\bmigrationBuilder\s*\.\s*Sql\s*\(")
RE_UP_METHOD = re.compile(r"\bvoid\s+Up\s*\(\s*MigrationBuilder\b")

STRING_MASK = "\x01"  # đánh dấu "chỗ này từng là string literal"


def blank(mask: list[str], start: int, end: int, fill: str) -> None:
    """Ghi đè [start, end) bằng `fill`, GIỮ NGUYÊN newline để số dòng không lệch."""
    for i in range(start, end):
        if mask[i] != "\n":
            mask[i] = fill


def tokenize(src: str) -> tuple[str, list[tuple[int, int, str]]]:
    """Che comment + string literal, trả (masked, strings).

    `masked` dài đúng bằng `src` nên mọi offset tìm được trên bản mask dùng thẳng
    được trên bản gốc (tra số dòng, cắt đoạn). Che TRƯỚC rồi mới parse là điều
    kiện tiên quyết: `AddSubscriptionsF8.cs` có chuỗi `migrationBuilder.Sql()`
    nằm trong **comment XML doc**, và `HashInvitationTokenDb23.cs` có `{...}`
    nằm TRONG chuỗi nội suy — cả hai sẽ đánh lừa mọi cách đếm ngây thơ.

    `strings` = [(start, end, value)] với `value` là nội dung đã bỏ delimiter.
    """
    n = len(src)
    mask = list(src)
    strings: list[tuple[int, int, str]] = []
    i = 0

    while i < n:
        c = src[i]

        if c == "/" and i + 1 < n and src[i + 1] == "/":
            end = src.find("\n", i)
            end = n if end == -1 else end
            blank(mask, i, end, " ")
            i = end
            continue

        if c == "/" and i + 1 < n and src[i + 1] == "*":
            end = src.find("*/", i + 2)
            end = n if end == -1 else end + 2
            blank(mask, i, end, " ")
            i = end
            continue

        if c == "'":  # char literal: `'"'` sẽ mở một string GIẢ nếu không che
            j = i + 1
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == "'":
                    j += 1
                    break
                j += 1
            blank(mask, i, j, " ")
            i = j
            continue

        if c == '"':
            # Quét NGƯỢC qua cụm `@$` — prefix có thể là `@"`, `$"`, `$@"`, `@$"`,
            # nên nhìn đúng 1 ký tự trước là sai với hai dạng sau.
            start = i
            while start > 0 and src[start - 1] in "@$":
                start -= 1
            prefix = src[start:i]
            verbatim = "@" in prefix
            interpolated = "$" in prefix

            if not verbatim and src.startswith('"""', i):
                quotes = 0
                while i + quotes < n and src[i + quotes] == '"':
                    quotes += 1
                body_start = i + quotes
                fence = '"' * quotes
                close = src.find(fence, body_start)
                if close == -1:
                    body_end, end = n, n
                else:
                    body_end, end = close, close + quotes
                value = src[body_start:body_end]
            elif verbatim:
                j = i + 1
                while j < n:
                    if src[j] == '"':
                        if j + 1 < n and src[j + 1] == '"':  # `""` = escape
                            j += 2
                            continue
                        break
                    j += 1
                value = src[i + 1 : j].replace('""', '"')
                end = min(j + 1, n)
            else:
                j = i + 1
                depth = 0  # độ sâu `{}` của chuỗi nội suy
                while j < n:
                    ch = src[j]
                    if ch == "\\":
                        j += 2
                        continue
                    if interpolated and ch == "{":
                        if j + 1 < n and src[j + 1] == "{":
                            j += 2
                            continue
                        depth += 1
                    elif interpolated and ch == "}":
                        if j + 1 < n and src[j + 1] == "}":
                            j += 2
                            continue
                        depth = max(0, depth - 1)
                    elif ch == '"' and depth == 0:
                        break
                    j += 1
                value = src[i + 1 : j]
                end = min(j + 1, n)

            strings.append((start, end, value))
            blank(mask, start, end, STRING_MASK)
            i = end
            continue

        i += 1

    return "".join(mask), strings


def match_close(masked: str, open_pos: int, opener: str, closer: str) -> int:
    """Vị trí ký tự đóng khớp với `opener` ở `open_pos` (-1 nếu không cân)."""
    depth = 0
    for i in range(open_pos, len(masked)):
        if masked[i] == opener:
            depth += 1
        elif masked[i] == closer:
            depth -= 1
            if depth == 0:
                return i
    return -1


def find_up_body(masked: str) -> tuple[int, int] | None:
    """Khoảng [start, end) của thân `Up(MigrationBuilder …)`.

    Bắt buộc giới hạn ở `Up()`: `Down()` của một `AddColumn` luôn chứa
    `DropColumn` đối xứng, đếm cả file sẽ ra 196 thay vì 20 — con số vô nghĩa,
    và cảnh báo nhiễu tới mức không ai đọc nữa.
    """
    m = RE_UP_METHOD.search(masked)
    if not m:
        return None
    paren = masked.find("(", m.start())
    close_paren = match_close(masked, paren, "(", ")")
    if close_paren == -1:
        return None
    brace = masked.find("{", close_paren)
    if brace == -1:
        return None
    close_brace = match_close(masked, brace, "{", "}")
    if close_brace == -1:
        return None
    return brace + 1, close_brace


def line_of(src: str, offset: int) -> int:
    return src.count("\n", 0, offset) + 1


def first_argument(masked: str, start: int, end: int) -> tuple[int, int]:
    """Cắt đối số đầu tiên (tách ở dấu phẩy cấp 0) — `Sql(sql, suppress: true)`."""
    depth = 0
    for i in range(start, end):
        ch = masked[i]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == "," and depth == 0:
            return start, i
    return start, end


def sql_argument(
    masked: str, strings: list[tuple[int, int, str]], arg_start: int, arg_end: int
) -> str | None:
    """Ghép MỌI literal trong đối số; None nếu đối số không phải chuỗi thuần.

    9/17 lời gọi `Sql()` là chuỗi NỐI nhiều literal (`"…" + "…;"`), nên chỉ đọc
    literal đầu tiên là kết luận sai về ký tự cuối cùng của câu SQL.
    """
    parts = [v for (s, e, v) in strings if arg_start <= s and e <= arg_end]
    if not parts:
        return None
    leftover = masked[arg_start:arg_end].replace(STRING_MASK, "")
    if leftover.strip(" \t\r\n+"):  # còn identifier/biến => không kết luận
        return None
    return "".join(parts)


def annotate(level: str, rel: str, line: int, message: str) -> None:
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print(f"::{level} file={rel},line={line}::{message}")


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print(__doc__)
        return 2

    files: list[Path] = []
    for rel_dir in MIGRATION_DIRS:
        directory = REPO_ROOT / rel_dir
        if not directory.is_dir():
            continue
        for path in sorted(directory.glob("*.cs")):
            name = path.name
            if name.endswith(".Designer.cs") or name.endswith("ModelSnapshot.cs"):
                continue
            files.append(path)

    if not files:
        print("Không tìm thấy migration nào — sai thư mục?")
        for rel_dir in MIGRATION_DIRS:
            print(f"  {rel_dir}")
        return 2

    missing_semicolon: list[tuple[str, int, str]] = []
    destructive: list[tuple[str, int, str]] = []
    sql_calls = 0
    skipped_non_literal = 0

    for path in files:
        rel = path.relative_to(REPO_ROOT).as_posix()
        src = path.read_text(encoding="utf-8-sig")
        masked, strings = tokenize(src)

        body = find_up_body(masked)
        if body is None:
            continue
        up_start, up_end = body

        for m in RE_SQL_CALL.finditer(masked, up_start, up_end):
            open_paren = m.end() - 1
            close_paren = match_close(masked, open_paren, "(", ")")
            if close_paren == -1:
                continue
            sql_calls += 1
            arg_start, arg_end = first_argument(masked, open_paren + 1, close_paren)
            statement = sql_argument(masked, strings, arg_start, arg_end)
            if statement is None:
                skipped_non_literal += 1
                continue
            if not statement.rstrip().endswith(";"):
                tail = statement.rstrip()[-60:].replace("\n", " ")
                missing_semicolon.append((rel, line_of(src, m.start()), tail))

        for m in RE_DESTRUCTIVE.finditer(masked, up_start, up_end):
            destructive.append((rel, line_of(src, m.start()), m.group(1)))

    destructive_files = {rel for rel, _, _ in destructive}

    print(f"{'migration':<10}: {len(files)}")
    print(f"{'Sql()':<10}: {sql_calls}")
    print(
        f"{'phá huỷ':<10}: {len(destructive)} lần gọi / "
        f"{len(destructive_files)} migration"
    )
    print(f"{'thiếu `;`':<10}: {len(missing_semicolon)}")
    if skipped_non_literal:
        print(f"{'bỏ qua':<10}: {skipped_non_literal} Sql() đối số không phải literal")

    if destructive:
        print(f"\nTHAO TÁC PHÁ HUỶ trong Up() — xác nhận là có chủ đích: {len(destructive)}")
        print("  Deploy: apply migration TRƯỚC hoặc CÙNG lúc deploy image, không bao giờ SAU.")
        for rel, line, op in destructive:
            print(f"  {rel}:{line} {op}")
            annotate(
                "warning",
                rel,
                line,
                f"{op} trong Up() — image cũ còn map cột/bảng này sẽ trả 500 "
                f"(42703). Xác nhận thứ tự deploy.",
            )

    if missing_semicolon:
        print(f"\nRAW SQL THIẾU `;` CUỐI: {len(missing_semicolon)}")
        print("  Thiếu `;` làm vỡ `dotnet ef migrations script --idempotent` lúc deploy,")
        print("  dù `dotnet ef database update` vẫn chạy được (bài học AddAuditColumnsAndTypes).")
        for rel, line, tail in missing_semicolon:
            print(f"  {rel}:{line} …{tail}")
            annotate(
                "error",
                rel,
                line,
                "migrationBuilder.Sql() không kết thúc bằng `;` — vỡ idempotent "
                "script lúc apply lên production.",
            )
        print("\nFAIL — thêm `;` vào cuối câu SQL.")
        return 1

    print("\nPASS — mọi raw SQL trong Up() đều kết thúc bằng `;`.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
