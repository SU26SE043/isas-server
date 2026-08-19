#!/usr/bin/env python3
"""Đo: `/decide-next` có trả về ID tiêu chí ĐỌC ĐƯỢC không?

Log production: model trả `targetCriterionId='Giao tiếp & trình bày'` — TÊN chứ không phải GUID —
nên `Guid.TryParse` phía .NET trượt và mọi cập nhật bằng chứng bị bỏ. Hệ quả trong DB:
178 dòng UNKNOWN, 13 PARTIAL, 5 FAILED, **0 SATISFIED** — cơ chế lái follow-up bằng bằng chứng
chưa từng chạy.

Script này biến "đã sửa chưa" thành một CON SỐ. Chạy trên cây mã NỀN (lúc commit) để lấy mức nền,
rồi chạy lại trên cây đã siết prompt; so hai tỉ lệ. Không có nó thì bản vá prompt chỉ là niềm tin.

Chọn cây mã bằng biến môi trường APP_ROOT:
    APP_ROOT=/tmp/baseline   → prompt lúc commit (mức nền)
    APP_ROOT=/app            → prompt đang sửa
"""
import argparse
import asyncio
import json
import os
import sys
import uuid

sys.path.insert(0, os.environ.get("APP_ROOT", "/app"))

from app.config import settings          # noqa: E402
from app.providers import gemini as gm   # noqa: E402


def classify(returned, sent_state) -> str:
    """Model gọi tiêu chí bằng gì? Đây là toàn bộ nội dung phép đo."""
    if not returned or not str(returned).strip():
        return "trống"
    value = str(returned).strip()
    ids = {e["criterionId"] for e in sent_state}
    names = {(e["name"] or "").strip().lower() for e in sent_state}
    try:
        uuid.UUID(value)
    except ValueError:
        # Không phải GUID. Có phải TÊN ta vừa gửi không? Đó chính là ca đã quan sát trên prod.
        return "TÊN (đúng ca lỗi)" if value.lower() in names else "chuỗi lạ"
    return "GUID hợp lệ" if value in ids else "GUID lạ (bịa)"


async def run_one(provider, row, sem):
    async with sem:
        state = row["current_evidence_state"]
        try:
            decision = await provider.decide_next(
                job_category=row["job_category"],
                current_question=row["current_question"],
                transcript=row["transcript"],
                history=row.get("history") or [],
                asked_count=len(row.get("history") or []) + 1,
                follow_up_count=0,
                max_questions=row.get("max_questions") or 0,
                max_follow_ups=row.get("max_follow_ups") or 0,
                criteria=row.get("criteria") or [],
                root_question=row.get("root_question"),
                current_depth=row.get("current_depth") or 0,
                max_depth=row.get("max_depth") or 0,
                other_topics=[],
                seniority=row.get("seniority"),
                current_evidence_state=state,
            )
        except Exception as exc:  # noqa: BLE001 — một ca hỏng không được giết cả phép đo
            return {"loi": f"{type(exc).__name__}: {exc}"}
        return {
            "action": decision.get("action"),
            "phan_loai": classify(decision.get("targetCriterionId"), state),
            "gia_tri": str(decision.get("targetCriterionId") or "")[:60],
            "state_moi": decision.get("newEvidenceState"),
        }


async def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--fixture", default="/app/evidence-fixture.json")
    ap.add_argument("--limit", type=int, default=20)
    ap.add_argument("--concurrency", type=int, default=4)
    args = ap.parse_args()

    rows = [r for r in json.load(open(args.fixture, encoding="utf-8"))
            if r.get("current_evidence_state") and r.get("criteria") and r.get("transcript")]
    rows.sort(key=lambda r: r["answer_id"])
    if args.limit and args.limit < len(rows):          # lấy mẫu cách đều, tất định
        step = len(rows) / args.limit
        rows = [rows[int(i * step)] for i in range(args.limit)]

    gm.report_usage = lambda *a, **k: asyncio.sleep(0)   # không làm bẩn ai_usage_logs
    settings.usage_metering_enabled = False
    provider = gm.GeminiProvider()

    print(f"[*] cây mã: {os.environ.get('APP_ROOT', '/app')}")
    print(f"[*] {len(rows)} ca thật × 1 lượt Gemini\n")

    sem = asyncio.Semaphore(args.concurrency)
    results = await asyncio.gather(*(run_one(provider, r, sem) for r in rows))

    tally: dict[str, int] = {}
    actions: dict[str, int] = {}
    for res in results:
        key = res.get("loi") or res["phan_loai"]
        tally[key] = tally.get(key, 0) + 1
        if "action" in res:
            actions[res["action"] or "(trống)"] = actions.get(res["action"] or "(trống)", 0) + 1

    total = len(results)
    print("MODEL GỌI TIÊU CHÍ BẰNG GÌ:")
    for key, n in sorted(tally.items(), key=lambda kv: -kv[1]):
        print(f"   {key:<22} {n:>3}/{total}  ({100 * n / total:.0f}%)")
    good = tally.get("GUID hợp lệ", 0)
    print(f"\n→ TỈ LỆ DÙNG ĐƯỢC (GUID hợp lệ): {100 * good / total:.0f}%")
    print(f"\nACTION model chọn: {actions}")
    print("\nVài ca đầu:")
    for res in results[:6]:
        if "loi" in res:
            print(f"   ! {res['loi'][:90]}")
        else:
            print(f"   {res['phan_loai']:<22} action={res['action']:<12} '{res['gia_tri']}'")


if __name__ == "__main__":
    asyncio.run(main())
