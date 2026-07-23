#!/usr/bin/env python3
"""Kiểm merged OpenAPI doc của Gateway có khớp routing thật không.

Vì sao cần: `ApiServices` từng bị ghép lệch prefix (compose override theo index,
Prefix lấy từ appsettings) → Scalar liệt kê 119/144 endpoint sai đường dẫn mà
KHÔNG có gì báo lỗi: doc vẫn sinh ra, service vẫn chạy, chỉ có path là bịa.
Script này bắt đúng lớp bug đó: mọi op trong doc phải có route thật ở gateway.

Cách phân biệt (quan trọng — đừng rút gọn):
  - 404 body RỖNG      -> YARP không có route  => LỖI
  - 404 có JSON body   -> 404 nghiệp vụ, endpoint tồn tại => OK
  - 401/403/400/415/302/200 -> route tồn tại => OK

Chạy không kèm token và body `{}` nên mọi endpoint mutation dừng ở 401/400 —
không tạo/sửa/xoá dữ liệu.

    python3 scripts/verify-gateway-openapi.py https://<gateway>
"""

from __future__ import annotations

import concurrent.futures as cf
import json
import re
import sys
import urllib.error
import urllib.request

METHODS = ("get", "post", "put", "patch", "delete")
BODY_METHODS = ("POST", "PUT", "PATCH")
PARAM_SUBSTITUTES = {
    "jobCategory": "BE",
    "ownerType": "User",
    "token": "verify-gateway-openapi",
    "key": "verify-gateway-openapi",
    "format": "csv",
}
PLACEHOLDER_GUID = "11111111-1111-1111-1111-111111111111"
TIMEOUT_SECONDS = 25


def fill_path(path: str) -> str:
    return re.sub(
        r"\{(\w+)\}",
        lambda m: PARAM_SUBSTITUTES.get(m.group(1), PLACEHOLDER_GUID),
        path,
    )


def fetch_json(url: str):
    with urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:
        return json.load(response)


def probe(base: str, method: str, path: str) -> tuple[int, bytes]:
    data = b"{}" if method in BODY_METHODS else None
    request = urllib.request.Request(base + fill_path(path), data=data, method=method)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as err:
        return err.code, err.read()
    except Exception as err:  # noqa: BLE001 - mạng hỏng cũng phải báo, không nuốt
        return 0, str(err).encode()


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__)
        return 2

    base = argv[1].rstrip("/")
    doc = fetch_json(f"{base}/openapi/merged.json")

    operations = [
        (method.upper(), path)
        for path, item in sorted(doc.get("paths", {}).items())
        for method in item
        if method in METHODS
    ]

    with cf.ThreadPoolExecutor(max_workers=12) as pool:
        results = list(pool.map(lambda op: probe(base, *op), operations))

    missing_route, unreachable = [], []
    for (method, path), (status, body) in zip(operations, results):
        if status == 404 and not body.strip():
            missing_route.append(f"{method} {path}")
        elif status == 0:
            unreachable.append(f"{method} {path} -> {body.decode(errors='replace')[:80]}")

    print(f"gateway : {base}")
    print(f"ops     : {len(operations)}")
    print(f"schemas : {len(doc.get('components', {}).get('schemas', {}))}")
    print(f"ok      : {len(operations) - len(missing_route) - len(unreachable)}")

    for title, rows in (("KHÔNG CÓ ROUTE (404 body rỗng)", missing_route),
                        ("GỌI KHÔNG ĐƯỢC", unreachable)):
        if rows:
            print(f"\n{title}: {len(rows)}")
            for row in rows:
                print(f"  {row}")

    if missing_route or unreachable:
        print("\nFAIL — doc liệt kê endpoint không gọi được qua gateway.")
        return 1

    print("\nPASS — mọi endpoint trong doc đều có route thật.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
