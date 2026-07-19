# tests/test_usage_metering_f22.py — F22 (FR18): đo token/chi phí mỗi lượt gọi Gemini.
#
# Trước vòng này service gọi generate_content 10 lần và KHÔNG chỗ nào đọc usage_metadata
# ⇒ không ai biết hệ thống đốt bao nhiêu token. Bộ test này khoá hai điều:
#   (1) MỌI đường gọi đều được đo — không sót đường nào (sót = thiếu số liệu trong im lặng);
#   (2) việc đo KHÔNG BAO GIỜ làm hỏng đường chính (sink chết ⇒ vẫn chấm được).
import json
from unittest.mock import AsyncMock, patch

import pytest

from app.providers.gemini import GeminiProvider
from app.resources import count_rejected_urls
from app.usage import TokenUsage, extract_usage, report_usage


class _Usage:
    def __init__(self, prompt, output, total):
        self.prompt_token_count = prompt
        self.candidates_token_count = output
        self.total_token_count = total


def _resp(payload: dict | None = None, usage=_Usage(100, 50, 160), text: str | None = None):
    """Response giả: .text + .usage_metadata (thứ SDK thật trả về)."""
    r = AsyncMock()
    r.text = text if text is not None else json.dumps(payload or {})
    r.usage_metadata = usage
    return r


# ── extract_usage ────────────────────────────────────────────────────────────────

def test_extract_usage_doc_du_3_con_so():
    usage = extract_usage(_resp(usage=_Usage(1200, 340, 1600)))
    assert usage == TokenUsage(prompt_tokens=1200, output_tokens=340, total_tokens=1600)


def test_extract_usage_total_khong_phai_tong_hai_ve():
    # Gemini tính cả token nội bộ/suy luận ngoài prompt+candidates. Lấy total của SDK,
    # KHÔNG tự cộng — tự cộng là tự báo cáo thiếu.
    usage = extract_usage(_resp(usage=_Usage(100, 50, 900)))
    assert usage.total_tokens == 900


def test_extract_usage_khong_co_metadata_tra_none_khong_raise():
    r = AsyncMock()
    r.text = "{}"
    r.usage_metadata = None
    assert extract_usage(r) is None


def test_extract_usage_shape_la_khong_raise():
    # Mock/SDK đổi shape: int(Mock) ném TypeError. Đo KHÔNG được làm vỡ đường chính.
    class _Weird:
        prompt_token_count = object()
        candidates_token_count = "abc"
        total_token_count = None

    r = AsyncMock()
    r.usage_metadata = _Weird()
    assert extract_usage(r) is None


# ── MỌI đường gọi đều được đo ────────────────────────────────────────────────────

# (tên method, kwargs, payload LLM trả) — mỗi phần tử là một đường gọi Gemini có thật.
_ALL_CALLS = [
    ("generate", dict(job_category="BE", cv_text=None, jd_text=None),
     {"questions": ["Câu 1"]}),
    ("suggest_criteria", dict(job_category="BE", jd_text="JD", criteria_text=None, count=2),
     {"criteria": [{"name": "Kỹ thuật", "weight": 1.0, "maxScore": 5}]}),
    ("analyze_cv", dict(cv_text="CV", jd_text=None, job_category="BE"),
     {"summary": "Tóm tắt", "strengths": [], "weaknesses": [], "suggestions": []}),
    ("score", dict(question="Q", transcript="T", job_category="BE",
                   criteria=[{"criterionId": "c1", "name": "N", "maxScore": 5, "weight": 1.0,
                              "levels": [{"score": 5, "descriptor": "tốt"}]}]),
     {"scores": [{"criterionId": "c1", "score": 5, "levelMatched": 5, "reasoning": "vì X"}],
      "sampleAnswer": "mẫu"}),
    ("generate_roadmap", dict(job_category="BE", level="Junior", weaknesses=None, cv_text=None),
     {"milestones": [{"title": "M1", "focusCriteria": [], "lessons": [{"title": "L1"}]}]}),
    ("generate_lesson_theory", dict(job_category="BE", level="Junior", lesson_title="L",
                                    focus_criteria=[], weaknesses=None),
     {"theoryMarkdown": "# Lý thuyết", "resources": []}),
    ("summarize_roadmap", dict(job_category="BE", level="Junior", criteria_progress=[]),
     {"strengths": [], "weaknesses": [], "improvements": [], "overallComment": "Nhận xét"}),
    ("summarize_session", dict(job_category="BE", overall_score=80.0, criteria_scores=[]),
     {"overallComment": "Nhận xét"}),
    ("decide_next", dict(job_category="BE", current_question="Q", transcript="T", history=[],
                         asked_count=1, follow_up_count=0, max_questions=5, max_follow_ups=2,
                         criteria=[]),
     {"action": "end"}),
]


@pytest.mark.asyncio
@pytest.mark.parametrize("method,kwargs,payload", _ALL_CALLS,
                         ids=[c[0] for c in _ALL_CALLS])
async def test_moi_duong_goi_deu_ghi_nhan_usage(method, kwargs, payload):
    """Từng đường gọi Gemini PHẢI ghi nhận token — sót một đường = thiếu số liệu trong im lặng."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_resp(payload, usage=_Usage(700, 120, 830)))

    with patch("app.providers.gemini.report_usage", new=AsyncMock()) as spy:
        await getattr(provider, method)(**kwargs)

    spy.assert_awaited_once()
    args, call_kwargs = spy.await_args
    assert args[0] == (method if method != "generate" else "generate_questions")
    # Chính response vừa nhận được đưa vào đo (không phải object rỗng nào khác).
    assert args[2].usage_metadata.prompt_token_count == 700


@pytest.mark.asyncio
async def test_tts_ghi_nhan_theo_model_rieng():
    """TTS chạy model KHÁC (tts_model) và có bảng giá riêng — ghi nhầm model = tính sai tiền."""
    from app.config import settings

    provider = GeminiProvider()
    blob = type("B", (), {"data": b"pcm", "mime_type": "audio/L16"})()
    part = type("P", (), {"inline_data": blob})()
    content = type("C", (), {"parts": [part]})()
    candidate = type("Cd", (), {"content": content})()

    r = AsyncMock()
    r.candidates = [candidate]
    r.usage_metadata = _Usage(20, 900, 920)
    provider._client.aio.models.generate_content = AsyncMock(return_value=r)

    with patch("app.providers.gemini.report_usage", new=AsyncMock()) as spy:
        await provider.synthesize_speech("xin chào", voice="Kore", language_code="vi-VN")

    args, _ = spy.await_args
    assert args[0] == "text_to_speech"
    assert args[1] == settings.tts_model      # KHÔNG phải gemini_model
    assert args[1] != settings.gemini_model


@pytest.mark.asyncio
async def test_ghi_nhan_ca_khi_parse_that_bai():
    """Token đã bị đốt kể cả khi output malformed — mà đó lại là những lượt ĐẮT nhất
    (AI3 retry tới score_max_attempts lần). Hoãn ghi tới sau parse = mất đúng phần cần thấy."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_resp(text="{{{ không phải JSON", usage=_Usage(500, 10, 510)))

    with patch("app.providers.gemini.report_usage", new=AsyncMock()) as spy:
        with pytest.raises(ValueError):
            await provider.generate(job_category="BE", cv_text=None, jd_text=None)

    spy.assert_awaited_once()


@pytest.mark.asyncio
async def test_lesson_theory_ghi_nhan_ca_khi_parse_that_bai():
    """Đường lesson-theory hoãn ghi (defer_report) để đính kèm số liệu URL → try/finally BẮT BUỘC.
    Thiếu finally thì đúng đường duy nhất có ngoại lệ sẽ mất số liệu khi parse hỏng."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_resp(text="không-phải-json", usage=_Usage(800, 20, 820)))

    with patch("app.providers.gemini.report_usage", new=AsyncMock()) as spy:
        with pytest.raises(ValueError):
            await provider.generate_lesson_theory(
                job_category="BE", level="Junior", lesson_title="L",
                focus_criteria=[], weaknesses=None)

    spy.assert_awaited_once()


# ── F15: đếm URL bị allowlist loại ───────────────────────────────────────────────

def test_dem_url_bi_loai():
    """Allowlist F15 loại URL trong IM LẶNG — nếu Gemini bịa domain 90% số lần thì không ai biết."""
    stats = count_rejected_urls([
        {"title": "A", "url": "https://developer.mozilla.org/x"},   # hợp lệ
        {"title": "B", "url": "https://khong-co-that.example/y"},   # domain lạ → loại
        {"title": "C", "url": "http://developer.mozilla.org/z"},    # http → loại
        {"title": "D"},                                             # không đề xuất url
    ])
    assert stats == {"resourceUrlsProposed": 3, "resourceUrlsRejected": 2}


def test_dem_url_khong_de_xuat_tra_none():
    # 0/0 hiển thị thành "0% bị loại" là câu khẳng định ta không có cơ sở để nói.
    assert count_rejected_urls([{"title": "A"}]) is None
    assert count_rejected_urls(None) is None


@pytest.mark.asyncio
async def test_lesson_theory_dinh_kem_so_lieu_url():
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(return_value=_resp({
        "theoryMarkdown": "# Lý thuyết",
        "resources": [
            {"title": "A", "type": "Doc", "url": "https://developer.mozilla.org/x"},
            {"title": "B", "type": "Doc", "url": "https://bia-dat.example/y"},
        ],
    }))

    with patch("app.providers.gemini.report_usage", new=AsyncMock()) as spy:
        theory, resources = await provider.generate_lesson_theory(
            job_category="BE", level="Junior", lesson_title="L",
            focus_criteria=[], weaknesses=None)

    assert theory.startswith("#")
    _, call_kwargs = spy.await_args
    assert call_kwargs["meta"] == {"resourceUrlsProposed": 2, "resourceUrlsRejected": 1}


# ── Đo KHÔNG được làm hỏng đường chính ───────────────────────────────────────────

@pytest.mark.asyncio
async def test_sink_chet_khong_lam_hong_luot_cham(monkeypatch):
    """Lỗi khi ghi số liệu KHÔNG được làm answer Failed — đó là biến tính năng quan sát
    thành đường mất credit (PAY-13)."""
    monkeypatch.setattr("app.config.settings.usage_sink_base", "http://sink-khong-ton-tai:9")

    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(return_value=_resp({
        "scores": [{"criterionId": "c1", "score": 5, "levelMatched": 5, "reasoning": "vì X"}],
        "sampleAnswer": "mẫu",
    }))

    outcome = await provider.score(
        question="Q", transcript="T", job_category="BE",
        criteria=[{"criterionId": "c1", "name": "N", "maxScore": 5, "weight": 1.0,
                   "levels": [{"score": 5, "descriptor": "tốt"}]}])

    # Chấm vẫn ra kết quả dù sink không tồn tại.
    assert outcome.scores[0]["score"] == 5.0


@pytest.mark.asyncio
async def test_report_usage_nuot_moi_loi(monkeypatch):
    monkeypatch.setattr("app.config.settings.usage_sink_base", "http://sink:1")

    class _Boom:
        def __getattr__(self, _):
            raise RuntimeError("mạng hỏng")

    monkeypatch.setitem(__import__("sys").modules, "aiohttp", _Boom())
    # Không raise là toàn bộ khẳng định của test này.
    await report_usage("score", "gemini-2.5-flash", _resp())


@pytest.mark.asyncio
async def test_tat_metering_thi_khong_lam_gi(monkeypatch):
    """Kill-switch: sự cố sink không phải lý do để deploy lại AIService."""
    monkeypatch.setattr("app.config.settings.usage_metering_enabled", False)
    monkeypatch.setattr("app.config.settings.usage_sink_base", "http://sink-khong-ton-tai:9")
    await report_usage("score", "gemini-2.5-flash", _resp())
