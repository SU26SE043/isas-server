#!/usr/bin/env python3
"""Đo trần đồng thời của AIService để chỉnh `AI_THREAD_POOL` (`THREAD_POOL_MAX_WORKERS`).

## Vì sao có script này

Mọi việc nặng của AIService đi qua executor mặc định của event loop (`asyncio.to_thread`):
tải S3, giải mã audio, và lời gọi chép lời ĐỒNG BỘ. Cỡ mặc định là `min(32, cpu+4)` = **12**
trên server 8 core, nên 12 chính là số request `/decide-next` chạy song song được — bất kể
mạng và nhà cung cấp còn dư bao nhiêu.

Nới pool KHÔNG phải lúc nào cũng có lợi: nếu nút thắt thật là băng thông upload thì pool lớn
hơn chỉ biến "từ chối nhanh" thành "hàng đợi dài". Nên phải ĐO chứ không đoán.

## Nhắm `/transcribe`, không nhắm `/decide-next`

Cả hai dùng CÙNG một `asyncio.to_thread(transcriber.transcribe_detailed, …)` — tức cùng cái
pool ta đang đo. Nhưng `/decide-next` gọi thêm Gemini, mà Gemini dùng client **async**
(`_client.aio.…`) nên **không giữ thread**: nó chỉ cộng thêm tiền và nhiễu vào phép đo.
`/transcribe` cho cùng câu trả lời, rẻ hơn, và không đẻ tác dụng phụ nào lên buổi phỏng vấn.

## Chạy

Chạy TRONG container (cần `app.storage` để lấy audio, và để không lẫn độ trễ mạng của client):

    docker cp scripts/loadtest-ai-threadpool.py aiapi-main:/tmp/lt.py
    docker exec aiapi-main python /tmp/lt.py --key "answer-audio/<...>.webm" --concurrency 8

Quét nhiều mức:

    for k in 4 8 12 16 24; do docker exec aiapi-main python /tmp/lt.py --key "..." -c $k; done

## ⚠ Đọc kết quả

- Script **FAIL** nếu bất kỳ response nào trả `transcriptEngine` bắt đầu bằng `local:`. Đó là
  dấu hiệu nhà cung cấp từ xa đã từ chối (429/timeout) và bản chép rơi về Whisper cục bộ —
  nó nạp model 778 MB và ngốn CPU, nên MỌI số đo từ thời điểm đó trở đi là rác. Không phải
  cảnh báo cho vui: đây là cách phép đo tự bịa ra một "trần" không có thật.
- Chạy trên container ĐANG PHỤC VỤ traffic thật ⇒ chọn giờ thấp điểm, đừng để K quá cao lâu.
"""
import argparse
import asyncio
import io
import statistics
import sys
import time

sys.path.insert(0, "/app")

import httpx  # noqa: E402


async def one(client: httpx.AsyncClient, url: str, blob: bytes, name: str,
              headers: dict[str, str]) -> tuple[float, str | None, int]:
    t0 = time.perf_counter()
    files = {"file": (name, io.BytesIO(blob), "audio/webm")}
    try:
        r = await client.post(url, files=files, params={"language": "vi"},
                              headers=headers, timeout=300.0)
        engine = r.json().get("transcriptEngine") if r.status_code == 200 else None
        return time.perf_counter() - t0, engine, r.status_code
    except Exception as ex:                       # timeout / reset — đếm là lỗi, không làm hỏng cả lượt
        print(f"    lỗi: {type(ex).__name__}: {ex}", file=sys.stderr)
        return time.perf_counter() - t0, None, 0


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--key", required=True, help="S3 object key của audio thật (tải 1 lần, tái dùng)")
    ap.add_argument("--base", default="http://localhost:8000")
    ap.add_argument("-c", "--concurrency", type=int, default=8)
    ap.add_argument("-n", "--requests", type=int, default=0, help="mặc định = 2 × concurrency")
    args = ap.parse_args()

    from app import storage
    from app.config import settings
    blob = storage.get_object_bytes(args.key)
    total = args.requests or args.concurrency * 2
    url = f"{args.base}/api/v1/transcribe"
    # Q2/GEN-7: /transcribe nay gate X-Internal-Token (fail-closed). Script chạy TRONG container
    # nên đọc thẳng cùng `settings` mà service dùng — thiếu header thì mọi lượt về 401 và bảng số
    # đo sẽ ra "lỗi=n" chứ không phải một phép đo.
    headers = {"X-Internal-Token": settings.internal_token}

    sem = asyncio.Semaphore(args.concurrency)

    async def guarded(client):
        async with sem:
            return await one(client, url, blob, args.key.rsplit("/", 1)[-1], headers)

    async with httpx.AsyncClient() as client:
        t0 = time.perf_counter()
        results = await asyncio.gather(*(guarded(client) for _ in range(total)))
        wall = time.perf_counter() - t0

    lat = sorted(r[0] for r in results if r[2] == 200)
    engines = {r[1] for r in results if r[1]}
    bad = [r for r in results if r[2] != 200]
    fell_back = sorted(e for e in engines if e.startswith("local:"))

    print(f"K={args.concurrency:>3}  n={total:>3}  "
          f"wall={wall:6.2f}s  thông_lượng={len(lat) / wall:5.2f} req/s  "
          f"p50={statistics.median(lat) if lat else float('nan'):6.2f}s  "
          f"p95={lat[int(len(lat) * 0.95)] if len(lat) > 1 else (lat[0] if lat else float('nan')):6.2f}s  "
          f"lỗi={len(bad)}  engine={sorted(engines) or '—'}")

    if fell_back:
        print(f"  ✗ RƠI VỀ CỤC BỘ ({', '.join(fell_back)}) — số đo từ đây KHÔNG dùng được.",
              file=sys.stderr)
        return 1
    if bad:
        print(f"  ✗ {len(bad)} request không 200 (mã: {sorted({r[2] for r in bad})}).", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
