#!/usr/bin/env python3
"""A/B trần thinking cho `/generate-lesson-theory` (và `/generate-roadmap`) — chạy IN-PROCESS.

Vì sao in-process chứ không qua HTTP: cần đổi `thinking_budget` theo TỪNG lượt trên cùng một
process, và cần đọc `usage_metadata.thoughts_token_count` + `finish_reason` của từng lượt Gemini
(kể cả lượt bị rubric trả lại) — HTTP chỉ trả bài cuối. Chạy trong container aiapi để dùng đúng
key/model/prompt registry của môi trường đó:

  docker cp scripts/benchmark-lesson-theory.py aiapi-main:/tmp/bench.py
  docker cp lessons.json aiapi-main:/tmp/lessons.json
  docker exec -w /app aiapi-main python /tmp/bench.py /tmp/lessons.json \
      --budgets -1,2048,1024,512,0 --runs 2 --out /tmp/bench.json

Fixture `lessons.json` = mảng bài THẬT (rút từ `roadmap_lessons` ⋈ milestones ⋈ roadmaps +
`roadmap_mistakes`), mỗi phần tử: jobCategory · language · mode · roadmapLevel · lessonTitle ·
focusCriteria · baseline · grounding · mistakes[{id,criterionName,question,answer,reasoning,
sampleAnswer,seniority}]. Chứa câu trả lời ứng viên ⇒ KHÔNG commit fixture.

Cách đo tôn trọng đúng đường production: gọi `provider.generate_lesson_theory` (rubric + retry +
sanitize resources y hệt), chỉ chen `thinking_config` vào `config` ngay trước `_generate` — nên
chạy được cả trên image CŨ chưa có setting `lesson_theory_thinking_budget`. Kết quả từng lượt:
elapsed · prompt/candidates/thoughts token · finish_reason · rubric lượt 1 · số ký tự · số mục.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import statistics
import sys
import time

from google.genai import types

import app.providers.gemini as gm
from app.config import settings

# Cắt trần y hệt AiServiceRoadmapGenerator.cs (MIS1-B5) — đo bằng prompt production, không phải
# prompt to hơn.
_CAPS = {"question": 260, "answer": 400, "reasoning": 350, "sampleAnswer": 700}
_LEVELS = ["Fresher", "Junior", "Middle", "Senior"]
# Gemini 2.5 Flash, USD / 1M token (input · output+thinking) — chỉ để in ước lượng, không ghi DB.
_PRICE_IN, _PRICE_OUT = 0.30, 2.50


def _trunc(s, n):
    if not isinstance(s, str):
        return s
    return s if len(s) <= n else s[:n]


def _weaknesses(fx: dict) -> list[str] | None:
    """RoadmapLessonService.FilterWeakCriteria + FormatWeaknesses: baseline ∩ focus, pct < 50."""
    baseline = fx.get("baseline") or {}
    out = [f"{name}: {baseline[name]:g}%" for name in fx.get("focusCriteria") or []
           if name in baseline and float(baseline[name]) < 50]
    return out or None


def _level(fx: dict) -> str:
    """ResolveLessonSeniorityAsync: max seniority của các lỗi bài bám, không có thì mức roadmap."""
    sens = [m.get("seniority") for m in fx.get("mistakes") or [] if m.get("seniority") in _LEVELS]
    if not sens:
        return fx["roadmapLevel"]
    return max(sens, key=_LEVELS.index)


def _mistakes(fx: dict) -> list[dict] | None:
    ms = []
    for m in fx.get("mistakes") or []:
        ms.append({
            "id": m["id"], "criterionName": m["criterionName"],
            "question": _trunc(m.get("question"), _CAPS["question"]),
            "answer": _trunc(m.get("answer"), _CAPS["answer"]),
            "reasoning": _trunc(m.get("reasoning"), _CAPS["reasoning"]),
            "sampleAnswer": _trunc(m.get("sampleAnswer"), _CAPS["sampleAnswer"]),
        })
    return ms[:3] or None


async def run_one(provider, fx: dict, budget: int, attempts_log: list[dict], defects_log: list) -> dict:
    orig_generate = provider._generate
    orig_eval = gm.evaluate_lesson_theory

    async def spy_generate(operation, **kw):
        cfg = kw.get("config")
        if budget >= 0 and cfg is not None:
            cfg.thinking_config = types.ThinkingConfig(thinking_budget=budget)
        t0 = time.perf_counter()
        resp = await orig_generate(operation, **kw)
        meta = getattr(resp, "usage_metadata", None)
        attempts_log.append({
            "elapsed": round(time.perf_counter() - t0, 2),
            "prompt": getattr(meta, "prompt_token_count", None),
            "candidates": getattr(meta, "candidates_token_count", None),
            "thoughts": getattr(meta, "thoughts_token_count", None),
            "diag": gm._generation_diagnostics(resp),
        })
        return resp

    def spy_eval(data, focus, title, **kw):
        d = orig_eval(data, focus, title, **kw)
        defects_log.append(list(d))
        return d

    provider._generate = spy_generate
    gm.evaluate_lesson_theory = spy_eval
    t0 = time.perf_counter()
    try:
        res = await provider.generate_lesson_theory(
            fx["jobCategory"], _level(fx), fx["lessonTitle"], list(fx.get("focusCriteria") or []),
            _weaknesses(fx), grounding=fx.get("grounding") or None,
            language=fx.get("language", "vi"), mode=fx.get("mode", "LevelUp"),
            mistakes=_mistakes(fx))
        ok, err = True, None
    except Exception as ex:  # noqa: BLE001 — bench: ghi lại lỗi, không dừng cả battery
        res, ok, err = None, False, f"{type(ex).__name__}: {ex}"[:300]
    finally:
        provider._generate = orig_generate
        gm.evaluate_lesson_theory = orig_eval
    total = round(time.perf_counter() - t0, 2)
    theory = res.theory if res else ""
    return {
        "ok": ok, "error": err, "total": total,
        "chars": len(theory), "sections": theory.count("\n## "),
        "resources": len(res.resources) if res else 0,
        "cited": len(res.cited_chunk_ids or []) if res else 0,
        "mistake_review": len(res.mistake_review or []) if res else 0,
        # Đạt ngay lượt 1 = KHÔNG có lượt viết lại nào (rubric + phủ lỗi mistakeReview đều qua).
        "first_pass": ok and len(attempts_log) == 1,
        "attempts": len(attempts_log),
    }


def _p(xs, q):
    xs = sorted(x for x in xs if x is not None)
    if not xs:
        return None
    k = max(0, min(len(xs) - 1, round((len(xs) - 1) * q)))
    return xs[k]


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("fixture")
    ap.add_argument("--budgets", default="-1,2048,1024,512,0")
    ap.add_argument("--runs", type=int, default=2)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    lessons = json.load(open(args.fixture, encoding="utf-8"))
    budgets = [int(b) for b in args.budgets.split(",")]
    provider = gm.GeminiProvider()
    records: list[dict] = []
    print(f"model={settings.gemini_model} lessons={len(lessons)} budgets={budgets} runs={args.runs}",
          file=sys.stderr)

    for budget in budgets:
        for fx in lessons:
            for run in range(args.runs):
                attempts: list[dict] = []
                defects: list = []
                r = await run_one(provider, fx, budget, attempts, defects)
                r.update({"budget": budget, "lesson": fx["lessonTitle"][:60], "job": fx["jobCategory"],
                          "lang": fx.get("language", "vi"), "run": run, "attempt_log": attempts,
                          "defects": defects})
                records.append(r)
                a0 = attempts[0] if attempts else {}
                print(f"budget={budget:>5} {fx['jobCategory']}/{r['lang']} run={run} ok={r['ok']} "
                      f"total={r['total']:.1f}s attempts={r['attempts']} first_pass={r['first_pass']} "
                      f"chars={r['chars']} sections={r['sections']} res={r['resources']} "
                      f"cited={r['cited']} review={r['mistake_review']} "
                      f"thoughts={a0.get('thoughts')} cand={a0.get('candidates')} {a0.get('diag','')}"
                      + (f" ERR={r['error']}" if r["error"] else ""), file=sys.stderr)

    print("\n=== TỔNG KẾT theo ngân sách (n = lessons × runs) ===")
    base = [r for r in records if r["budget"] == budgets[0]]
    base_chars = _p([r["chars"] for r in base if r["ok"]], 0.5) or 1
    for budget in budgets:
        rs = [r for r in records if r["budget"] == budget]
        oks = [r for r in rs if r["ok"]]
        thoughts = [a["thoughts"] for r in rs for a in r["attempt_log"]]
        cost = [((a["prompt"] or 0) * _PRICE_IN + ((a["candidates"] or 0) + (a["thoughts"] or 0)) * _PRICE_OUT) / 1e6
                for r in rs for a in r["attempt_log"]]
        print(f"budget={budget:>5} ok={len(oks)}/{len(rs)} "
              f"p50={_p([r['total'] for r in rs], 0.5)}s p90={_p([r['total'] for r in rs], 0.9)}s "
              f"first_pass={sum(r['first_pass'] for r in rs)}/{len(rs)} "
              f"retry_lượt={sum(r['attempts'] - 1 for r in rs)} "
              f"thoughts_p50={_p(thoughts, 0.5)} "
              f"chars_p50={_p([r['chars'] for r in oks], 0.5)} ({(_p([r['chars'] for r in oks], 0.5) or 0) / base_chars:.0%} baseline) "
              f"sections_p50={_p([r['sections'] for r in oks], 0.5)} "
              f"review_tổng={sum(r['mistake_review'] for r in oks)} "
              f"$/bài≈{statistics.mean(cost) if cost else 0:.4f}")
    if args.out:
        json.dump(records, open(args.out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"ghi {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
