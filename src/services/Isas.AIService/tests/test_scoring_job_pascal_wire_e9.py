# tests/test_scoring_job_pascal_wire_e9.py — job chấm THẬT (PascalCase) phải ra đúng prompt/mốc/số đo.
#
# 🔴 VÌ SAO CÓ FILE NÀY: job chấm đi qua RabbitMQ được `ScoringJobPublisher.cs` tuần tự hoá KHÔNG kèm
# options ⇒ khoá PascalCase (`{"Score":0,"Descriptor":"…"}`). Mọi test cũ (`test_scoring.py`…) tự gõ
# dict camelCase, nên suốt từ E9 (2026-07-12):
#   - prompt chấm thật in "• Mức None: " không mô tả, `score()` rơi về dải 0..maxScore ⇒ AI cho 7
#     khi HR khai {0,5,10}, C# phải snap lại; còn chấm thử (HTTP camelCase) vẫn thấy mốc ⇒ CHẤM THỬ ≠
#     CHẤM THẬT, đúng thứ CAMP-19 cấm;
#   - số đo cách nói in "chưa đo được" cho mọi chỉ số (9 lần so với 1 trên job thật).
#
# File mẫu `fixtures/scoring_job_pascal.json` KHÔNG gõ tay: nó do chính `ScoringJobPublisher.Serialize`
# sinh ra (`ScoringJobWireFixtureTests` phía Interview so từng byte). Đổi hình dạng job bên .NET ⇒
# test C# đỏ trước, buộc sinh lại file mẫu và chạy lại file này.
import copy
import json
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import aio_pika

from app import worker
from app.fluency import Segment, compute_delivery_metrics
from app.main import build_preview_scoring_criteria
from app.prompts import build_delivery_block, build_scoring_prompt
from app.providers.gemini import GeminiProvider, ScoreOutcome
from app.schemas import PreviewCriterion

FIXTURE = Path(__file__).parent / "fixtures" / "scoring_job_pascal.json"
MISSING = "chưa đo được"


def _job() -> dict:
    return json.loads(FIXTURE.read_text(encoding="utf-8"))


def _camel(obj):
    """Bản camelCase của TIÊU CHÍ (đường chấm thử). Đệ quy được vì trong tiêu chí không có dict dữ
    liệu nào — KHÁC số đo cách nói, nơi khoá `fillerBreakdown` là dữ liệu và không được đụng."""
    if isinstance(obj, list):
        return [_camel(x) for x in obj]
    if isinstance(obj, dict):
        return {k[0].lower() + k[1:]: _camel(v) for k, v in obj.items()}
    return obj


def _prompt(criteria, delivery=None, job=None):
    job = job or _job()
    return build_scoring_prompt(
        job["QuestionContent"], job["Transcript"], job["JobCategory"], criteria, delivery,
        language=job["Language"], sample_answer=job["SampleAnswer"])


# ── Bản thân file mẫu: phải là dạng hàng đợi THẬT, không phải dạng ta mong ─────────────────
def test_file_mau_la_PascalCase_that_va_co_du_ca_can_khoa():
    job = _job()
    crit = job["Criteria"][0]
    assert set(crit["Levels"][0]) == {"Score", "Descriptor"}
    assert set(crit["Anchors"][0]) == {"Score", "ExampleAnswer"}
    assert [lv["Score"] for lv in crit["Levels"]] == [0, 5, 10], "cần mốc 0 để bắt bẫy truthiness"
    assert job["DeliveryMetrics"]["FillerBreakdown"], "cần FillerBreakdown khác rỗng"
    assert "SpeechRateWpm" in job["DeliveryMetrics"]


# ── Mốc điểm trong prompt chấm ──────────────────────────────────────────────────────────────
def test_prompt_tu_job_that_in_du_mo_ta_tung_moc_va_cau_neo():
    job = _job()
    prompt = _prompt(job["Criteria"])

    assert "Mức None" not in prompt
    assert "Ví dụ mức None" not in prompt
    for crit in job["Criteria"]:
        for lv in crit["Levels"]:
            assert f'    • Mức {lv["Score"]}: {lv["Descriptor"]}' in prompt
        for an in crit["Anchors"] or []:
            assert f'    ↳ Ví dụ mức {an["Score"]}: {an["ExampleAnswer"]}' in prompt


def test_prompt_PascalCase_GIONG_HET_camelCase():
    """Cùng tiêu chí, hai quy ước casing ⇒ MỘT prompt. Lệch một byte là hai thước đo."""
    job = _job()
    assert _prompt(job["Criteria"]) == _prompt(_camel(job["Criteria"]))


def test_duong_cham_thu_ra_prompt_GIONG_HET_duong_hang_doi():
    """CAMP-19: chấm thử phải LÀ chấm thật. Đi đúng đường của chấm thử — `PreviewCriterion` (pydantic)
    → `build_preview_scoring_criteria` — rồi so với job hàng đợi. Chấm thử không mang câu neo nên bỏ
    `Anchors` ở cả hai vế."""
    job = _job()
    queue = copy.deepcopy(job["Criteria"])
    for c in queue:
        c["Anchors"] = None

    preview = build_preview_scoring_criteria([
        PreviewCriterion(
            criterionId=c["CriterionId"], name=c["Name"], description=c["Description"],
            maxScore=c["MaxScore"], weight=c["Weight"],
            levels=[{"score": lv["Score"], "descriptor": lv["Descriptor"]} for lv in c["Levels"]],
            expectedWeak=0, expectedGood=c["Levels"][1]["Score"], expectedExcellent=c["MaxScore"],
        )
        for c in job["Criteria"]
    ])

    assert _prompt(queue) == _prompt(preview)


# ── Mốc điểm trong lượt snap của score() ────────────────────────────────────────────────────
def _provider_tra(scores: list[dict]) -> GeminiProvider:
    provider = GeminiProvider()
    resp = AsyncMock()
    resp.text = json.dumps({"scores": scores})
    provider._client.aio.models.generate_content = AsyncMock(return_value=resp)
    return provider


async def _cham_lech(criteria) -> dict[str, int]:
    """LLM trả điểm LỆCH mốc: 7 (HR khai {0,5,10}) và 1 không kèm mức (HR khai {0,2,5})."""
    c1, c2 = (c.get("CriterionId") or c.get("criterionId") for c in criteria)
    provider = _provider_tra([
        {"criterionId": c1, "score": 7, "levelMatched": 7, "reasoning": "dùng Redis cache-aside"},
        {"criterionId": c2, "score": 1, "reasoning": "trình bày còn lan man"},
    ])
    outcome = await provider.score("Q", "trả lời", "BE", criteria)
    return {s["criterionId"]: s["levelMatched"] for s in outcome.scores}


async def test_score_job_that_snap_ve_DUNG_tap_moc_HR_khai():
    job = _job()
    c1, c2 = (c["CriterionId"] for c in job["Criteria"])

    levels = await _cham_lech(job["Criteria"])

    # 7 gần 5 hơn 10; 1 cách đều 0 và 2 ⇒ tie-break mức thấp = 0 (chứng minh mốc 0 có mặt).
    assert levels == {c1: 5, c2: 0}, "điểm phải rơi vào tập mốc HR khai, không phải dải 0..maxScore"


async def test_score_camelCase_snap_GIONG_HET_PascalCase():
    job = _job()
    assert await _cham_lech(_camel(job["Criteria"])) == await _cham_lech(job["Criteria"])


# ── Số đo cách nói ──────────────────────────────────────────────────────────────────────────
def test_whitelist_khop_dung_khoa_fluency_va_khoa_cua_job_dotnet():
    text = "dạ em nghĩ là ừm cache"
    fluency_keys = set(compute_delivery_metrics(text, [Segment(0.0, 2.0, text)], 3.0).to_dict())
    dotnet_keys = {k[0].lower() + k[1:] for k in _job()["DeliveryMetrics"]}

    assert set(worker.DELIVERY_METRIC_KEYS) == fluency_keys
    assert set(worker.DELIVERY_METRIC_KEYS) == dotnet_keys


def test_chuan_hoa_so_do_tu_job_that_thanh_camelCase_khong_doi_khoa_du_lieu():
    raw = _job()["DeliveryMetrics"]
    out = worker.normalize_delivery_metrics(raw)

    assert set(out) == set(worker.DELIVERY_METRIC_KEYS)
    assert out["speechRateWpm"] == raw["SpeechRateWpm"]
    assert out["metricsVersion"] == raw["MetricsVersion"]
    assert out["fillerBreakdown"] == raw["FillerBreakdown"], "khoá từ đệm là DỮ LIỆU, giữ nguyên"


def test_chuan_hoa_KHONG_de_quy_vao_fillerBreakdown():
    """Một từ đệm trùng tên field (vd ứng viên lặp từ "SpeechSec") vẫn là DỮ LIỆU: đổi khoá đệ quy
    sẽ biến nó thành `speechSec` và làm sai nhãn từ đệm hiện cho người dùng."""
    out = worker.normalize_delivery_metrics(
        {"PauseCount": 2, "FillerBreakdown": {"SpeechSec": 1, "Ừm": 2}})

    assert out == {"pauseCount": 2, "fillerBreakdown": {"SpeechSec": 1, "Ừm": 2}}


def test_khoi_so_do_tu_job_that_KHONG_con_chua_do_duoc():
    job = _job()
    block = build_delivery_block(worker.normalize_delivery_metrics(job["DeliveryMetrics"]),
                                 language=job["Language"])

    # 1 lần duy nhất là câu "LƯU Ý: chỉ số nào ghi …" — không chỉ số nào khuyết.
    assert block.count(MISSING) == 1
    assert "226.9" in block and "2.4s" in block and '"ừm" ×3' in block


def test_khoi_so_do_PascalCase_GIONG_HET_camelCase():
    raw = _job()["DeliveryMetrics"]
    camel = {k[0].lower() + k[1:]: v for k, v in raw.items()}   # chỉ tầng đầu
    assert build_delivery_block(worker.normalize_delivery_metrics(raw)) == build_delivery_block(camel)


async def test_worker_dua_so_do_DA_CHUAN_HOA_vao_luot_cham_va_callback(monkeypatch):
    """Ranh giới thật: job nguyên văn từ file mẫu → `process_message` → `provider.score(delivery=…)`."""
    job = _job()
    captured: dict = {}

    async def fake_post(payload):
        captured.update(payload)

    score = AsyncMock(return_value=ScoreOutcome(scores=[], sample_answer=None))
    monkeypatch.setattr(worker, "post_callback", fake_post)
    monkeypatch.setattr(worker.provider, "score", score)
    monkeypatch.setattr(worker, "maybe_report_multi_voice", AsyncMock())

    message = MagicMock(spec=aio_pika.IncomingMessage)
    message.body = json.dumps(job).encode()
    message.ack = AsyncMock()
    message.nack = AsyncMock()

    await worker.process_message(message)

    message.ack.assert_awaited_once()
    delivery = score.await_args.kwargs["delivery"]
    assert build_delivery_block(delivery).count(MISSING) == 1
    assert delivery["fillerBreakdown"] == job["DeliveryMetrics"]["FillerBreakdown"]
    assert score.await_args.kwargs["criteria"] == job["Criteria"]
    assert captured["deliveryMetrics"]["pauseCount"] == job["DeliveryMetrics"]["PauseCount"]
