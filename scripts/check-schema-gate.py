#!/usr/bin/env python3
"""So migration trong repo với migration ĐÃ ÁP trên production.

Vì sao cần: hai lần (02/08 và 05/08) CI deploy image mới trước khi migration
được apply ⇒ code hỏi cột chưa tồn tại ⇒ `42703: column … does not exist` trên
đường request thật, suốt nhiều giờ, trong khi `/health` vẫn xanh (truy vấn health
dùng projection nên không chạm cột mới). Guard này biến "nhớ apply migration"
từ kỷ luật con người thành một phép so sánh chạy được.

MigrationId của EF = **tên file bỏ đuôi `.cs`** (vd `20260801064726_AddTrackB…`),
đúng thứ nằm trong `__EFMigrationsHistory`. Script chỉ so tên, không chạm DB —
vế production do `scripts/dump-prod-migrations.sh` lấy về (chỉ đọc, qua SSH).

Hai chiều lệch, CỐ Ý KHÔNG đối xứng:

  - `repo ∖ db` (repo có, DB chưa áp) -> **ĐỎ**. Đây đúng là hình dạng "code đi
    trước migration": merge xong CI deploy ngay, code mới hỏi cột chưa có.

  - `db ∖ repo` (DB có, repo không)  -> **CẢNH BÁO, KHÔNG chặn**. DB đi trước
    code là chuyện bình thường và cần thiết: rollback về commit cũ, hoặc
    migration đến từ nhánh khác. Code cũ hầu như luôn sống được với cột THỪA
    (nó chỉ không biết cột đó tồn tại). Chặn ở đây sẽ **khoá cứng đường
    rollback** — mà rollback chính là thứ đợt này đang dựng. Không đánh đổi cái
    đó lấy sự đối xứng cho đẹp.

Chế độ qua env `SCHEMA_GATE_MODE`:
  - `warn` (MẶC ĐỊNH) — in đủ thông tin nhưng luôn trả 0.
  - `enforce`         — `repo ∖ db` khác rỗng thì trả 1.

Mặc định `warn` là có chủ đích: khoảng 30% commit first-parent mang theo
migration, nên bật `enforce` ngay ngày đầu sẽ chặn xấp xỉ một phần ba số lần
merge cho tới khi "apply migration trước khi merge" thành thói quen. Cho nó
chạy ồn ào một thời gian rồi mới siết.

    ssh user@host 'bash -s' < scripts/dump-prod-migrations.sh > prod.txt
    python3 scripts/check-schema-gate.py prod.txt
    SCHEMA_GATE_MODE=enforce python3 scripts/check-schema-gate.py prod.txt
    ssh user@host 'bash -s' < scripts/dump-prod-migrations.sh | python3 scripts/check-schema-gate.py -

Lệch quy ước có chủ đích: trong GitHub Actions (`GITHUB_ACTIONS=true`) script in
thêm `::error::` / `::warning::` — cùng lý do và cùng tiền lệ như
`check-migrations.py` (`ci.yml` đã dùng `::error::` ở step "Verify AIService lock").
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# Scope CỨNG — cùng lý do như check-migrations.py: `.claude/worktrees/` chứa các
# bản sao TOÀN BỘ repo, `rglob` từ gốc sẽ đếm mỗi migration nhiều lần.
SERVICE_DIRS = {
    "auth": "src/services/Isas.AuthService/Migrations",
    "interview": "src/services/Isas.InterviewService/Migrations",
    "campaign": "src/services/Isas.CampaignService/Migrations",
    "payment": "src/services/Isas.PaymentService/Migrations",
}

EF_PROJECTS = {
    "auth": "src/services/Isas.AuthService",
    "interview": "src/services/Isas.InterviewService",
    "campaign": "src/services/Isas.CampaignService",
    "payment": "src/services/Isas.PaymentService",
}


def repo_migrations(service: str) -> set[str]:
    """MigrationId trong repo = tên file bỏ `.cs`."""
    directory = REPO_ROOT / SERVICE_DIRS[service]
    if not directory.is_dir():
        return set()
    return {
        path.name[: -len(".cs")]
        for path in directory.glob("*.cs")
        if not path.name.endswith(".Designer.cs")
        and not path.name.endswith("ModelSnapshot.cs")
    }


def parse_dump(text: str) -> tuple[dict[str, set[str]], dict[str, str], list[str]]:
    """(applied theo service, tên cột theo service, danh sách lỗi #err)."""
    applied: dict[str, set[str]] = {}
    columns: dict[str, str] = {}
    errors: list[str] = []

    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("#db"):
            parts = line.split()
            if len(parts) >= 4:
                service = parts[1]
                columns[service] = parts[3]
                applied.setdefault(service, set())
            continue
        if line.startswith("#err"):
            errors.append(line[len("#err") :].strip())
            continue
        if "|" in line:
            service, migration_id = line.split("|", 1)
            applied.setdefault(service.strip(), set()).add(migration_id.strip())

    return applied, columns, errors


def annotate(level: str, message: str) -> None:
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print(f"::{level}::{message}")


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__)
        return 2

    source = argv[1]
    if source == "-":
        text = sys.stdin.read()
    else:
        path = Path(source)
        if not path.is_file():
            print(f"Không đọc được file dump: {source}")
            return 2
        text = path.read_text(encoding="utf-8")

    applied, columns, errors = parse_dump(text)

    if not applied and not errors:
        print("Dump RỖNG — không có dòng `#db`/`#err` nào.")
        print("Chạy: ssh user@host 'bash -s' < scripts/dump-prod-migrations.sh")
        return 2

    mode = os.environ.get("SCHEMA_GATE_MODE", "warn").strip().lower()
    if mode not in ("warn", "enforce"):
        print(f"SCHEMA_GATE_MODE không hợp lệ: {mode!r} (chỉ nhận warn|enforce)")
        return 2

    rows: list[tuple[str, int, int, int, int, str]] = []
    missing_all: list[tuple[str, str]] = []  # repo có, DB chưa áp => ĐỎ
    extra_all: list[tuple[str, str]] = []  # DB có, repo không   => cảnh báo

    for service in SERVICE_DIRS:
        in_repo = repo_migrations(service)
        if service not in applied:
            continue  # DB này không nằm trong dump (xem #err bên dưới)
        in_db = applied[service]
        missing = sorted(in_repo - in_db)
        extra = sorted(in_db - in_repo)
        missing_all.extend((service, m) for m in missing)
        extra_all.extend((service, m) for m in extra)
        rows.append(
            (service, len(in_repo), len(in_db), len(missing), len(extra),
             columns.get(service, "?"))
        )

    print(f"{'chế độ':<10}: {mode}" + ("  (mặc định — chưa chặn merge)" if mode == "warn" else ""))
    print(f"{'DB đọc được':<10}: {len(rows)}/{len(SERVICE_DIRS)}")
    print()
    # Cột `cột` in tên cột MigrationId đọc được từ marker `#db`: nhìn là biết
    # bản dump đã TRA tên cột thật chứ không đoán (Auth "MigrationId" PascalCase
    # vs 3 service kia migration_id).
    print(f"{'service':<10} {'repo':>5} {'db':>5} {'thiếu':>6} {'thừa':>5}  cột")
    for service, n_repo, n_db, n_missing, n_extra, column in rows:
        print(f"{service:<10} {n_repo:>5} {n_db:>5} {n_missing:>6} {n_extra:>5}  {column}")

    if errors:
        print(f"\nKHÔNG ĐỌC ĐƯỢC DB: {len(errors)}")
        for err in errors:
            print(f"  {err}")
            annotate("error", f"Không đọc được lịch sử migration: {err}")

    if extra_all:
        print(f"\nDB CÓ, REPO KHÔNG (cảnh báo — rollback/nhánh khác, không chặn): {len(extra_all)}")
        for service, migration_id in extra_all:
            print(f"  {service}  {migration_id}")
            annotate(
                "warning",
                f"{service}: DB đã áp `{migration_id}` mà repo không có "
                f"(rollback hoặc migration từ nhánh khác).",
            )

    if missing_all:
        print(f"\nREPO CÓ, DB CHƯA ÁP: {len(missing_all)}")
        print("  Deploy code mang migration này TRƯỚC khi apply = 42703 trên đường request.")
        by_service: dict[str, list[str]] = {}
        for service, migration_id in missing_all:
            by_service.setdefault(service, []).append(migration_id)
        for service, ids in by_service.items():
            for migration_id in ids:
                print(f"  {service}  {migration_id}")
            print(f"    khắc phục: dotnet ef database update --project {EF_PROJECTS[service]}")
            annotate(
                "error" if mode == "enforce" else "warning",
                f"{service}: {len(ids)} migration chưa apply lên production "
                f"({', '.join(ids)}). Apply TRƯỚC hoặc CÙNG lúc deploy.",
            )

    if errors:
        # "Không đọc được DB" KHÔNG được coi là an toàn: ta không biết gì về
        # schema production, mà không-biết thì không được kết luận là khớp.
        print("\nFAIL — có DB không đọc được, không kết luận được schema production.")
        return 1

    if missing_all:
        if mode == "enforce":
            print("\nFAIL — apply migration rồi chạy lại.")
            return 1
        print("\nWARN — chế độ `warn` nên không chặn; đặt SCHEMA_GATE_MODE=enforce để chặn.")
        return 0

    print("\nPASS — production đã áp đủ mọi migration có trong repo.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
