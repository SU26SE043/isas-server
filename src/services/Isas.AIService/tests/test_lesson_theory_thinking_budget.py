"""Trần suy luận ẩn cho /generate-lesson-theory và /generate-roadmap.

Vì sao có test này: hai đường này từng là hai đường sinh CUỐI CÙNG để Gemini tự quyết thinking
(analyze_cv / score / decide_next / jd_requirements đều đã có trần). Đo prod 2026-09-14: bài giảng
38–52s với ~nửa output token là suy luận ẩn; A/B trên aiapi-dev 2026-09-15 cho thấy cùng một bài
có lượt 1.109 thoughts, lượt 6.862 thoughts — độ trễ không đoán được nếu để model tự quyết.
Khoá ba thứ (khuôn tests/test_jd_requirements_thinking_budget.py): (1) giá trị MẶC ĐỊNH — thứ có
hiệu lực trên deploy nếu không ai đặt env (bài học `multi_voice`: mọi test monkeypatch cờ nên mặc
định không ai phủ); (2) trần được đưa vào config gửi Gemini, phần còn lại của config KHÔNG đổi;
(3) `-1` là kill-switch trả về hành vi cũ (không gửi thinking_config).
"""
import json
from types import SimpleNamespace

import pytest

from app.config import Settings, settings
from app.providers.gemini import GeminiProvider

LESSON_DEFAULT = 1024
ROADMAP_DEFAULT = 1024

_ROADMAP_JSON = json.dumps({
    "milestoneCount": 1, "milestoneCountReason": "x",
    "milestones": [{"title": "M", "focusCriteria": ["A"], "lessons": [{"title": "L"}]}],
})


def test_mac_dinh_lesson_theory_khong_phai_tu_quyet():
    # Đọc từ model_fields, KHÔNG đọc instance: file test khác có thể monkeypatch instance.
    assert Settings.model_fields["lesson_theory_thinking_budget"].default == LESSON_DEFAULT


def test_mac_dinh_roadmap_khong_phai_tu_quyet():
    assert Settings.model_fields["roadmap_thinking_budget"].default == ROADMAP_DEFAULT


async def _lesson_config(monkeypatch, lesson_theory_payload) -> dict:
    provider = GeminiProvider()
    captured: dict = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["operation"] = operation
        captured["config"] = config
        return SimpleNamespace(text=json.dumps(lesson_theory_payload(["A"])))

    monkeypatch.setattr(provider, "_generate", fake_generate)
    await provider.generate_lesson_theory("BE", "Junior", "Bài", ["A"], None)
    return captured


async def _roadmap_config(monkeypatch) -> dict:
    provider = GeminiProvider()
    captured: dict = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["operation"] = operation
        captured["config"] = config
        return SimpleNamespace(text=_ROADMAP_JSON)

    monkeypatch.setattr(provider, "_generate", fake_generate)
    await provider.generate_roadmap("BE", "Junior", None, None)
    return captured


@pytest.mark.asyncio
async def test_lesson_tran_duoc_gui_vao_gemini(monkeypatch, lesson_theory_payload):
    monkeypatch.setattr(settings, "lesson_theory_thinking_budget", 1024)
    captured = await _lesson_config(monkeypatch, lesson_theory_payload)
    assert captured["operation"] == "generate_lesson_theory"
    assert captured["config"].thinking_config.thinking_budget == 1024
    # Phần còn lại của config KHÔNG đổi: vẫn temp 0.5 + JSON theo schema 3 phần bắt buộc.
    assert captured["config"].temperature == 0.5
    assert captured["config"].response_mime_type == "application/json"
    assert captured["config"].response_schema["required"] == ["sections", "example", "commonMistakes"]


def test_mac_dinh_tran_output_bai_giang_12288():
    """Lưới an toàn cho lượt chạy loạn (A/B 2026-09-15: 64.768 token/254s; dev cùng ngày: lượt thứ hai
    chạm trần sau 65s) — mặc định phải BẬT, và phải lớn hơn bài dài nhất + trần thinking (~6,5k)."""
    assert Settings.model_fields["lesson_theory_max_output_tokens"].default == 12288


@pytest.mark.asyncio
async def test_lesson_tran_output_duoc_gui_vao_gemini(monkeypatch, lesson_theory_payload):
    monkeypatch.setattr(settings, "lesson_theory_max_output_tokens", 4096)
    captured = await _lesson_config(monkeypatch, lesson_theory_payload)
    assert captured["config"].max_output_tokens == 4096


@pytest.mark.asyncio
async def test_lesson_tran_output_0_khong_cat(monkeypatch, lesson_theory_payload):
    monkeypatch.setattr(settings, "lesson_theory_max_output_tokens", 0)
    captured = await _lesson_config(monkeypatch, lesson_theory_payload)
    assert captured["config"].max_output_tokens is None


@pytest.mark.asyncio
async def test_lesson_tran_0_tat_thinking(monkeypatch, lesson_theory_payload):
    monkeypatch.setattr(settings, "lesson_theory_thinking_budget", 0)
    captured = await _lesson_config(monkeypatch, lesson_theory_payload)
    assert captured["config"].thinking_config.thinking_budget == 0


@pytest.mark.asyncio
async def test_lesson_tru_1_quay_lui_ve_model_tu_quyet(monkeypatch, lesson_theory_payload):
    monkeypatch.setattr(settings, "lesson_theory_thinking_budget", -1)
    captured = await _lesson_config(monkeypatch, lesson_theory_payload)
    assert captured["config"].thinking_config is None


@pytest.mark.asyncio
async def test_lesson_moi_luot_viet_lai_cung_mang_tran(monkeypatch, lesson_theory_payload):
    """Lượt bị rubric trả lại dùng lại CÙNG config — trần phải có ở lượt 2, không chỉ lượt 1."""
    monkeypatch.setattr(settings, "lesson_theory_thinking_budget", 512)
    provider = GeminiProvider()
    seen: list = []
    calls = {"n": 0}

    async def fake_generate(operation, *, contents, config, **kwargs):
        calls["n"] += 1
        seen.append(config.thinking_config.thinking_budget if config.thinking_config else None)
        payload = lesson_theory_payload(["A"]) if calls["n"] == 1 else lesson_theory_payload(["A", "B"])
        return SimpleNamespace(text=json.dumps(payload))

    monkeypatch.setattr(provider, "_generate", fake_generate)
    await provider.generate_lesson_theory("BE", "Junior", "Bài", ["A", "B"], None)
    assert seen == [512, 512]


@pytest.mark.asyncio
async def test_roadmap_tran_duoc_gui_vao_gemini(monkeypatch):
    monkeypatch.setattr(settings, "roadmap_thinking_budget", 1024)
    captured = await _roadmap_config(monkeypatch)
    assert captured["operation"] == "generate_roadmap"
    assert captured["config"].thinking_config.thinking_budget == 1024
    assert captured["config"].temperature == 0.4
    assert captured["config"].response_mime_type == "application/json"
    assert captured["config"].response_schema["required"] == [
        "milestoneCount", "milestoneCountReason", "milestones"]


@pytest.mark.asyncio
async def test_roadmap_tran_0_tat_thinking(monkeypatch):
    monkeypatch.setattr(settings, "roadmap_thinking_budget", 0)
    captured = await _roadmap_config(monkeypatch)
    assert captured["config"].thinking_config.thinking_budget == 0


@pytest.mark.asyncio
async def test_roadmap_tru_1_quay_lui_ve_model_tu_quyet(monkeypatch):
    monkeypatch.setattr(settings, "roadmap_thinking_budget", -1)
    captured = await _roadmap_config(monkeypatch)
    assert captured["config"].thinking_config is None
