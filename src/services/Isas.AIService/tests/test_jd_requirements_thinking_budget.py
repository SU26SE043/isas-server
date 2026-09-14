"""Trần suy luận ẩn cho /suggest-jd-requirements.

Vì sao có test này: đây từng là endpoint DUY NHẤT trong nhóm (analyze_cv / score / decide_next
đều đã có trần) để Gemini tự quyết thinking. Đo prod 2026-09-14: 9,8–37,3s, JD tiếng Việt luôn
vượt 20s timeout của FE ⇒ FE báo "tách yêu cầu quá lâu" trong khi AIService vẫn trả 200 và tiền
đã tính. Khoá ba thứ: (1) giá trị MẶC ĐỊNH — là thứ có hiệu lực trên deploy nếu không ai đặt env
(bài học `multi_voice`: mọi test monkeypatch cờ nên mặc định không ai phủ); (2) trần được đưa vào
config gửi Gemini; (3) `-1` là kill-switch trả về hành vi cũ.
"""
from types import SimpleNamespace

import pytest

from app.config import Settings, settings
from app.providers.gemini import GeminiProvider

_MODEL_JSON = '{"mustHave": [{"text": "C#", "citations": [], "jdQuote": null}], "niceToHave": []}'


def test_mac_dinh_1024_khong_phai_tu_quyet():
    # Đọc từ model_fields, KHÔNG đọc instance: file test khác có thể monkeypatch instance.
    assert Settings.model_fields["jd_requirements_thinking_budget"].default == 1024


async def _captured_config(monkeypatch) -> dict:
    provider = GeminiProvider()
    captured: dict = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["operation"] = operation
        captured["config"] = config
        return SimpleNamespace(text=_MODEL_JSON)

    monkeypatch.setattr(provider, "_generate", fake_generate)
    await provider.suggest_jd_requirements("Yêu cầu: thành thạo C#", "BE")
    return captured


@pytest.mark.asyncio
async def test_tran_duoc_gui_vao_gemini(monkeypatch):
    monkeypatch.setattr(settings, "jd_requirements_thinking_budget", 1024)
    captured = await _captured_config(monkeypatch)
    assert captured["operation"] == "suggest_jd_requirements"
    assert captured["config"].thinking_config.thinking_budget == 1024
    # Phần còn lại của config KHÔNG đổi: vẫn temp 0 + JSON theo schema.
    assert captured["config"].temperature == 0.0
    assert captured["config"].response_mime_type == "application/json"
    assert set(captured["config"].response_schema["required"]) == {"mustHave", "niceToHave"}


@pytest.mark.asyncio
async def test_tran_0_tat_thinking(monkeypatch):
    monkeypatch.setattr(settings, "jd_requirements_thinking_budget", 0)
    captured = await _captured_config(monkeypatch)
    assert captured["config"].thinking_config.thinking_budget == 0


@pytest.mark.asyncio
async def test_tru_1_quay_lui_ve_model_tu_quyet(monkeypatch):
    monkeypatch.setattr(settings, "jd_requirements_thinking_budget", -1)
    captured = await _captured_config(monkeypatch)
    assert captured["config"].thinking_config is None
