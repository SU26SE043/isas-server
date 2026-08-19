#!/usr/bin/env python3
"""A/B chấm lại câu trả lời THẬT để chứng minh "nhanh mà vẫn đúng" trước khi đổi cấu hình chấm.

Vì sao cần: `score()` là đường gọi Gemini duy nhất của service chưa đặt trần suy luận ẩn. Đo trên
production (`ai_usage_logs`, 2026-08-20): operation `score` có output p50 **3.570 token**, trong khi
`decide_next` — đã đặt `thinking_budget=0` — chỉ **126**. `output_tokens = candidates + thoughts`
(`app/usage.py`) nên con số đã gộp suy luận. Nội dung nhìn thấy được, đo thẳng trong DB (reasoning
1.144 ký tự + sampleAnswer 1.168 ký tự, p50) chỉ khoảng 900-1.000 token ⇒ **~2.500 token là
thinking**, và decode là tuần tự nên đó chính là phần lớn 19,6s p50 của một lượt chấm.

Đặt trần cho nó là đổi THƯỚC ĐO — điểm đang dùng để xếp hạng ứng viên (CAMP-10) và đo cải thiện
theo thời gian (BC15). Script này là cổng kiểm bắt buộc trước khi bật.

⚠ ĐIỂM MẤU CHỐT — SO VỚI CÁI GÌ.
   Muốn đo TÁI LẬP thì chạy CÙNG cấu hình hai lần rồi so hai lần với nhau (`--budgets=512,512`).
   So một lượt chấm mới với điểm production ĐÃ LƯU thì lẫn cả trôi-giữa-hai-phiên-bản-image vào
   kết quả — chính chỗ này đã một lần cho ra con số 32% sai lệch. Baseline trong fixture chỉ dùng
   để đối chiếu ĐỘ LỚN, không dùng để kết luận về tái lập.

⚠ SÀN NHIỄU khi so hai CẤU HÌNH khác nhau.
   Baseline trong fixture là điểm production đã chấm bằng CẤU HÌNH HIỆN TẠI. Chấm lại cùng cấu
   hình đó vẫn ra lệch (temperature=0 không làm LLM tất định tuyệt đối). Nên luôn chạy ÍT NHẤT hai
   pass: `-1` (giữ nguyên hiện trạng) để đo SÀN NHIỄU, rồi mới tới trần định thử. Kết luận đúng là
   "lệch của pass mới có tệ hơn sàn nhiễu không", KHÔNG phải "lệch của pass mới có nhỏ không" —
   so với 0 là tự đặt ra một chuẩn mà chính hiện trạng cũng trượt.

Đi qua ĐÚNG `provider.score()` + `build_scoring_prompt` mà ứng viên thật đi qua. Không dựng đường
thứ hai: một harness chấm bằng prompt khác thì nó kiểm chứng một hệ không tồn tại.

CHẠY (trong container, nơi có sẵn google-genai + GEMINI_API_KEY):

    scp scripts/benchmark-scoring.py duc2834@<host>:/tmp/
    scp scoring-fixture.json         duc2834@<host>:/tmp/
    ssh duc2834@<host> "docker cp /tmp/benchmark-scoring.py aiapi-main:/tmp/ \
        && docker cp /tmp/scoring-fixture.json aiapi-main:/tmp/ \
        && docker exec -w /app aiapi-main python /tmp/benchmark-scoring.py \
             --fixture /tmp/scoring-fixture.json --limit 60 --budgets -1,512"

Fixture dựng bằng `scripts/export-scoring-fixture.sql` (chỉ SELECT).

⚠ TIỀN: mỗi answer × mỗi budget = một lượt Gemini. `--limit 60 --budgets -1,512` = 120 lượt.
⚠ Script TỰ TẮT ghi nhận tiêu thụ (F22) để không làm bẩn chính `ai_usage_logs` đang dùng để
   nghiệm thu — nhưng vẫn đọc `usage_metadata` để báo cáo token.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import math
import os
import statistics
import sys
import time
from collections import defaultdict

# Container chạy WORKDIR=/app còn package nằm ở /app/app; python chỉ tự thêm thư mục CỦA SCRIPT
# (/tmp) vào sys.path, nên phải tự chèn app root vào.
sys.path.insert(0, os.environ.get("APP_ROOT", os.getcwd()))

from app.config import settings  # noqa: E402
from app.providers import gemini as gemini_module  # noqa: E402


# ── Dựng lại đầu vào ĐÚNG như production ─────────────────────────────────────────────────

# Bậc chất lượng của dải `Descriptive` — PHẢI khớp TỪNG CHỮ với `ScoringCriteriaBuilder`
# (`DescriptorsVi`/`DescriptorsEn`). Lệch một dấu là prompt đo được KHÁC prompt production, và
# con số benchmark trở thành số của một hệ không tồn tại.
DESCRIPTORS_VI = [
    "Không đáp ứng — không trả lời được, lạc đề, hoặc sai bản chất vấn đề.",
    "Yếu — có chạm vào chủ đề nhưng thiếu phần lớn ý cốt lõi, không có dẫn chứng.",
    "Trung bình — nêu được ý cốt lõi ở mức cơ bản, còn thiếu chiều sâu và dẫn chứng cụ thể.",
    "Khá — đủ ý cốt lõi và có dẫn chứng, nhưng chưa đầy đủ hoặc chưa nói tới đánh đổi.",
    "Tốt — đủ ý và có chiều sâu, dẫn chứng cụ thể từ kinh nghiệm thật, có phân tích đánh đổi.",
    "Xuất sắc — đầy đủ, chính xác và sâu; dẫn chứng thuyết phục, nêu được cả đánh đổi lẫn trường hợp biên.",
]

DESCRIPTORS_EN = [
    "Not met — no answer, off-topic, or fundamentally wrong.",
    "Weak — touches the topic but misses most core points; no evidence given.",
    "Average — covers the core points at a basic level; lacks depth and concrete evidence.",
    "Good — covers the core points with evidence, but incomplete or no trade-offs discussed.",
    "Strong — complete and in depth, concrete evidence from real work, discusses trade-offs.",
    "Excellent — complete, accurate and deep; convincing evidence, covers trade-offs and edge cases.",
]

MAX_DEFAULT_BAND_LEVELS = 6

# Kiểu dải đang đo. Đặt từ `--band`; mặc định = hiện trạng production (`Scoring:DefaultBandStyle`
# mặc định `EveryInteger`), để chạy không cờ vẫn ra đúng sàn nhiễu cũ mà so.
BAND_STYLE = "every-integer"


def _round_half_up(x: float) -> int:
    """C# `Math.Round(..., MidpointRounding.AwayFromZero)`; `round()` của Python là ToEven."""
    return int(math.floor(x + 0.5))


def default_band(max_score: int, language: str, style: str = "every-integer") -> list[dict]:
    """Sao y `ScoringCriteriaBuilder.DefaultBand` (C#).

    `rubric_levels` trên production đang RỖNG hoàn toàn (0 dòng) nên MỌI tiêu chí đi nhánh này —
    tức hàm này quyết định prompt của 100% lượt chấm, và nó phải khớp C# tuyệt đối.

    ``every-integer`` (mặc định, = hành vi production hiện tại): liệt kê từng số nguyên
    0..maxScore với descriptor rỗng nghĩa ("Mức 3/5").

    ``descriptive`` (opt-in, `Scoring:DefaultBandStyle=Descriptive`): ≤6 mốc trải đều kèm bậc chất
    lượng độc lập thang. ĐÂY LÀ THỨ CẦN NGHIỆM THU — chạy pass này rồi so tỉ lệ đổi mức với sàn
    ĐÃ ĐO (40 câu thật, chạy hai pass giống hệt rồi so hai pass với nhau): dải cũ 90,7% cặp cùng
    điểm, dải mới 92,1% — chênh 1,4 điểm phần trăm, sai số chuẩn của hiệu ≈ 2,7 ⇒ KHÔNG cải thiện
    đo được. Giả thuyết "mốc rỗng nghĩa làm chấm mất tái lập" SAI; cờ giữ TẮT.

    Khi WS-D nạp mức thật, thêm cột `levels` vào fixture và bỏ hàm này.
    """
    top = max(max_score, 0)

    if style != "descriptive":
        word = "Level" if language == "en" else "Mức"
        return [{"score": i, "descriptor": f"{word} {i}/{top}"} for i in range(top + 1)]

    labels = DESCRIPTORS_EN if language == "en" else DESCRIPTORS_VI
    count = min(MAX_DEFAULT_BAND_LEVELS, top + 1)
    if count <= 1:
        return [{"score": 0, "descriptor": labels[0]}]

    out = []
    for i in range(count):
        score = _round_half_up(i * top / (count - 1))
        # Nhãn: C# dùng MidpointRounding.ToEven ở đây = đúng `round()` mặc định của Python.
        label = labels[round(i * (len(labels) - 1) / (count - 1))]
        out.append({"score": score, "descriptor": label})
    return out


def to_criteria(row: dict) -> list[dict]:
    return [
        {
            "criterionId": c["criterionId"],
            "name": c["name"],
            "description": c.get("description") or "",
            "maxScore": c["maxScore"],
            "weight": c["weight"],
            "levels": default_band(c["maxScore"], row["language"], BAND_STYLE),
        }
        for c in row["criteria"]
    ]


# ── Đo ───────────────────────────────────────────────────────────────────────────────────

class UsageCapture:
    """Chặn `report_usage` lại: đọc số token nhưng KHÔNG gửi về Payment.

    Gửi thì chính bảng `ai_usage_logs` mà ta dùng để nghiệm thu sau deploy sẽ lẫn hàng trăm dòng
    benchmark — tự bịt mắt đúng cái đồng hồ mình sắp đọc.
    """

    def __init__(self) -> None:
        self.output_tokens: list[int] = []
        self.prompt_tokens: list[int] = []

    async def __call__(self, operation, model, response, meta=None) -> None:
        usage = gemini_module.extract_usage(response) if hasattr(gemini_module, "extract_usage") else None
        if usage is None:
            from app.usage import extract_usage
            usage = extract_usage(response)
        if usage is not None:
            self.output_tokens.append(usage.output_tokens)
            self.prompt_tokens.append(usage.prompt_tokens)


async def score_one(provider, row: dict, sem: asyncio.Semaphore) -> dict:
    async with sem:
        started = time.perf_counter()
        try:
            outcome = await provider.score(
                question=row["question"],
                transcript=row["transcript"],
                job_category=row["job_category"],
                criteria=to_criteria(row),
                temperature=0.0,          # attempt 1 của production
                delivery=row.get("delivery"),
                language=row["language"],
                sample_answer=None,       # B2C không có đáp án mẫu HR soạn
            )
        except Exception as exc:  # noqa: BLE001 — một câu hỏng không được giết cả phép đo
            return {"answer_id": row["answer_id"], "error": f"{type(exc).__name__}: {exc}",
                    "seconds": time.perf_counter() - started}
        return {
            "answer_id": row["answer_id"],
            "seconds": time.perf_counter() - started,
            "scores": {s["criterionId"]: float(s["score"]) for s in outcome.scores},
        }


async def run_pass(rows: list[dict], budget: int, concurrency: int) -> dict:
    if not hasattr(settings, "score_thinking_budget"):
        sys.exit("settings.score_thinking_budget chưa tồn tại — WS-A chưa deploy vào image này.")

    settings.score_thinking_budget = budget

    capture = UsageCapture()
    original_report = gemini_module.report_usage
    gemini_module.report_usage = capture
    original_metering = settings.usage_metering_enabled
    settings.usage_metering_enabled = False
    try:
        provider = gemini_module.GeminiProvider()
        sem = asyncio.Semaphore(concurrency)
        wall = time.perf_counter()
        results = await asyncio.gather(*(score_one(provider, r, sem) for r in rows))
        wall = time.perf_counter() - wall
    finally:
        gemini_module.report_usage = original_report
        settings.usage_metering_enabled = original_metering

    return {"budget": budget, "results": results, "wall_seconds": wall,
            "output_tokens": capture.output_tokens, "prompt_tokens": capture.prompt_tokens}


# ── Báo cáo ──────────────────────────────────────────────────────────────────────────────

def pct(values: list[float], q: float) -> float:
    if not values:
        return float("nan")
    ordered = sorted(values)
    idx = min(len(ordered) - 1, int(q * len(ordered)))
    return ordered[idx]


def compare(rows: list[dict], results: list[dict]) -> dict:
    """So điểm mới với baseline production, theo từng cặp (answer, tiêu chí)."""
    baseline = {
        r["answer_id"]: {b["criterionId"]: float(b["score"]) for b in r["baseline_scores"]}
        for r in rows
    }
    max_by_criterion = {
        c["criterionId"]: c["maxScore"] for r in rows for c in r["criteria"]
    }

    deltas: list[float] = []
    signed: list[float] = []
    per_criterion: dict[str, list[float]] = defaultdict(list)
    answers_shifted = 0
    scored_answers = 0
    missing = 0

    for res in results:
        if "error" in res:
            continue
        old = baseline.get(res["answer_id"], {})
        shifted = False
        seen_any = False
        for cid, new_score in res["scores"].items():
            if cid not in old:
                missing += 1
                continue
            seen_any = True
            # Chuẩn hoá về % thang: maxScore chạy từ 5 tới 30 giữa các rubric, cộng thẳng điểm
            # tuyệt đối là để rubric thang 30 lấn át hoàn toàn rubric thang 5.
            top = max(max_by_criterion.get(cid, 5), 1)
            d = (new_score - old[cid]) / top * 100.0
            deltas.append(abs(d))
            signed.append(d)
            per_criterion[cid].append(d)
            if abs(new_score - old[cid]) >= 1.0:
                shifted = True
        if seen_any:
            scored_answers += 1
            if shifted:
                answers_shifted += 1

    return {
        "pairs": len(deltas),
        "answers": scored_answers,
        "missing_criteria": missing,
        "abs_delta_pct_p50": statistics.median(deltas) if deltas else float("nan"),
        "abs_delta_pct_p90": pct(deltas, 0.9),
        "bias_pct_mean": statistics.fmean(signed) if signed else float("nan"),
        "answers_shifted_pct": 100.0 * answers_shifted / scored_answers if scored_answers else float("nan"),
        "worst_criteria": sorted(
            ((cid, statistics.fmean(v)) for cid, v in per_criterion.items() if len(v) >= 3),
            key=lambda kv: -abs(kv[1]),
        )[:5],
    }


def report(rows: list[dict], passes: list[dict]) -> None:
    print("\n" + "=" * 78)
    print("KẾT QUẢ — chấm lại câu trả lời THẬT")
    print("=" * 78)

    for p in passes:
        ok = [r for r in p["results"] if "error" not in r]
        errs = [r for r in p["results"] if "error" in r]
        secs = [r["seconds"] for r in ok]
        cmp_ = compare(rows, p["results"])
        label = "hiện trạng (SÀN NHIỄU)" if p["budget"] < 0 else f"trần thinking = {p['budget']}"

        print(f"\n── {label}")
        print(f"   chấm được          : {len(ok)}/{len(p['results'])}" + (f"  (lỗi {len(errs)})" if errs else ""))
        print(f"   giây/câu p50 · p90 : {pct(secs, 0.5):.1f} · {pct(secs, 0.9):.1f}")
        print(f"   output token p50   : {pct([float(x) for x in p['output_tokens']], 0.5):.0f}"
              f"   (p90 {pct([float(x) for x in p['output_tokens']], 0.9):.0f})")
        print(f"   input token p50    : {pct([float(x) for x in p['prompt_tokens']], 0.5):.0f}")
        print(f"   lệch |Δ| p50 · p90 : {cmp_['abs_delta_pct_p50']:.1f}% · {cmp_['abs_delta_pct_p90']:.1f}% thang")
        print(f"   lệch CÓ HƯỚNG      : {cmp_['bias_pct_mean']:+.2f}% thang")
        print(f"   câu đổi ≥1 mức     : {cmp_['answers_shifted_pct']:.1f}%")
        if errs:
            for e in errs[:3]:
                print(f"   ! {e['answer_id'][:8]}: {e['error'][:120]}")

    floor = next((p for p in passes if p["budget"] < 0), None)
    if floor is None or len(passes) < 2:
        print("\n(Chỉ có một pass — không có sàn nhiễu để đối chiếu. Chạy kèm `-1` mới kết luận được.)")
        return

    base = compare(rows, floor["results"])
    base_secs = [r["seconds"] for r in floor["results"] if "error" not in r]
    print("\n" + "-" * 78)
    print("ĐỐI CHIẾU VỚI SÀN NHIỄU")
    print("-" * 78)
    for p in passes:
        if p["budget"] < 0:
            continue
        cmp_ = compare(rows, p["results"])
        secs = [r["seconds"] for r in p["results"] if "error" not in r]
        speedup = pct(base_secs, 0.5) / pct(secs, 0.5) if pct(secs, 0.5) else float("nan")
        d_abs = cmp_["abs_delta_pct_p50"] - base["abs_delta_pct_p50"]
        d_bias = abs(cmp_["bias_pct_mean"]) - abs(base["bias_pct_mean"])
        d_shift = cmp_["answers_shifted_pct"] - base["answers_shifted_pct"]
        print(f"\n   trần {p['budget']}:")
        print(f"     nhanh hơn        : {speedup:.2f}×  ({pct(base_secs,0.5):.1f}s → {pct(secs,0.5):.1f}s)")
        print(f"     |Δ| p50 so sàn   : {d_abs:+.2f} điểm phần trăm")
        print(f"     |bias| so sàn    : {d_bias:+.2f} điểm phần trăm")
        print(f"     % câu đổi so sàn : {d_shift:+.1f} điểm phần trăm")
        verdict = "ĐẠT" if (d_abs <= 2.0 and d_bias <= 1.0 and d_shift <= 10.0) else "KHÔNG ĐẠT"
        print(f"     → {verdict}")
    print("\nNgưỡng: lệch không tệ hơn sàn nhiễu quá 2 điểm phần trăm, thiên lệch có hướng không")
    print("tăng quá 1, và tỉ lệ câu đổi mức không tăng quá 10. Chốt TRƯỚC khi chạy, đừng nới sau.")


# ── main ─────────────────────────────────────────────────────────────────────────────────

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--fixture", required=True, help="JSON từ scripts/export-scoring-fixture.sql")
    ap.add_argument("--limit", type=int, default=60,
                    help="số câu mỗi pass (lấy mẫu TẤT ĐỊNH, cách đều — mặc định 60)")
    ap.add_argument("--budgets", default="-1,512",
                    help="danh sách trần thinking, phẩy ngăn. -1 = hiện trạng (sàn nhiễu)")
    ap.add_argument("--concurrency", type=int, default=4)
    ap.add_argument("--band", choices=["every-integer", "descriptive"], default="every-integer",
                    help="dải mức mặc định gửi vào prompt — mirror `Scoring:DefaultBandStyle`. "
                         "every-integer = hiện trạng production; "
                         "descriptive = ≤6 mốc có nghĩa, dải đang chờ nghiệm thu")
    ap.add_argument("--out", help="ghi kết quả thô ra JSON")
    args = ap.parse_args()

    # `to_criteria` chạy sâu trong `score_one` (async, không cầm args) nên chốt ở module level.
    global BAND_STYLE
    BAND_STYLE = args.band

    with open(args.fixture, encoding="utf-8") as fh:
        rows = json.load(fh)
    rows = [r for r in rows if r.get("criteria") and r.get("baseline_scores") and r.get("transcript")]
    rows.sort(key=lambda r: r["answer_id"])

    # Lấy mẫu CÁCH ĐỀU thay vì `[:limit]`: fixture sắp theo id nên cắt đầu dễ dồn vào một nhóm
    # buổi/nghề. Tất định (không random) để hai lần chạy so được với nhau.
    if args.limit and args.limit < len(rows):
        step = len(rows) / args.limit
        rows = [rows[int(i * step)] for i in range(args.limit)]

    budgets = [int(b.strip()) for b in args.budgets.split(",") if b.strip()]
    print(f"[*] {len(rows)} câu × {len(budgets)} pass = {len(rows) * len(budgets)} lượt Gemini")
    print(f"[*] model={settings.gemini_model} concurrency={args.concurrency} band={BAND_STYLE}")

    passes = []
    for budget in budgets:
        print(f"\n[*] pass trần={budget} …")
        passes.append(asyncio.run(run_pass(rows, budget, args.concurrency)))

    report(rows, passes)

    if args.out:
        with open(args.out, "w", encoding="utf-8") as fh:
            json.dump({"fixture_size": len(rows), "passes": passes}, fh, ensure_ascii=False, indent=2)
        print(f"\n[*] kết quả thô: {args.out}")


if __name__ == "__main__":
    main()
