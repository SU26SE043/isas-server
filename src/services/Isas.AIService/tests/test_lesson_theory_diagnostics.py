# tests/test_lesson_theory_diagnostics.py — dòng `[⏱] lesson-theory attempt=…` cho MỌI lượt sinh.
#
# Vì sao: lượt `/generate-lesson-theory` mất 38–52s trên prod (2026-09-14) mà không ai biết bao nhiêu
# là suy luận ẩn, bao nhiêu lượt bị rubric trả lại — vì `logger.info` của app.* chưa bao giờ ra log
# (xem app/logging_setup.py) và đường này chưa từng đo thời gian. Ba tính chất khoá ở đây:
#   (1) mỗi lượt Gemini đúng MỘT dòng, có elapsed + finish_reason + thoughts_token_count
#   (2) lượt BỊ TRẢ LẠI cũng có dòng (log nằm trong `finally`) kèm số khiếm khuyết
#       — mutation: dời log sang nhánh thành công → ĐỎ
#   (3) `_generation_diagnostics` in được `thoughts_token_count`
#       — mutation: bỏ thoughts khỏi f-string → ĐỎ
import json
import logging

import pytest

from app.providers.gemini import GeminiProvider, _generation_diagnostics


def _resp(payload: dict, *, thoughts=7, candidates=50, finish="STOP"):
    meta = type("M", (), {"prompt_token_count": 100, "candidates_token_count": candidates,
                          "thoughts_token_count": thoughts, "total_token_count": 100 + candidates + thoughts})()
    cand = type("C", (), {"finish_reason": finish})()
    return type("R", (), {"text": json.dumps(payload), "candidates": [cand], "usage_metadata": meta})()


def _lines(caplog):
    return [r.getMessage() for r in caplog.records if "[⏱] lesson-theory" in r.getMessage()]


@pytest.mark.asyncio
async def test_moi_luot_sinh_co_dung_mot_dong_chan_doan(monkeypatch, caplog, lesson_theory_payload):
    async def fake_generate(self, operation, *, contents, config, model=None, defer_report=False):
        return _resp(lesson_theory_payload(["A"]), thoughts=7, candidates=50)

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    with caplog.at_level(logging.INFO, logger="app.providers.gemini"):
        await GeminiProvider().generate_lesson_theory("BE", "Junior", "Bài", ["A"], None)

    lines = _lines(caplog)
    assert len(lines) == 1
    line = lines[0]
    assert "attempt=1/2" in line
    assert "elapsed=" in line
    assert "finish_reason='STOP'" in line
    assert "candidates_token_count=50" in line
    assert "thoughts_token_count=7" in line
    assert "defects=0" in line
    assert 'lesson="Bài"' in line


@pytest.mark.asyncio
async def test_luot_bi_tra_lai_van_co_dong_chan_doan_kem_so_khiem_khuyet(
        monkeypatch, caplog, lesson_theory_payload):
    """Lượt đầu thiếu tiêu chí "B" (bị rubric trả lại), lượt hai đạt — PHẢI có HAI dòng, dòng đầu
    ghi defects>0. Đây là con số duy nhất cho biết tỉ lệ viết lại thật; log ở nhánh thành công thôi
    thì lượt tốn 50s bị trả lại biến mất khỏi mọi số đo."""
    calls = {"n": 0}

    async def fake_generate(self, operation, *, contents, config, model=None, defer_report=False):
        calls["n"] += 1
        if calls["n"] == 1:
            return _resp(lesson_theory_payload(["A"]), thoughts=900)      # thiếu "B"
        return _resp(lesson_theory_payload(["A", "B"]), thoughts=300)

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    with caplog.at_level(logging.INFO, logger="app.providers.gemini"):
        await GeminiProvider().generate_lesson_theory("BE", "Junior", "Bài", ["A", "B"], None)

    lines = _lines(caplog)
    assert len(lines) == 2
    assert "attempt=1/2" in lines[0] and "defects=1" in lines[0] and "thoughts_token_count=900" in lines[0]
    assert "attempt=2/2" in lines[1] and "defects=0" in lines[1]


@pytest.mark.asyncio
async def test_luot_khong_phai_json_van_co_dong_chan_doan(monkeypatch, caplog, lesson_theory_payload):
    calls = {"n": 0}

    async def fake_generate(self, operation, *, contents, config, model=None, defer_report=False):
        calls["n"] += 1
        if calls["n"] == 1:
            r = _resp({}, finish="MAX_TOKENS")
            r.text = "{không phải json"
            return r
        return _resp(lesson_theory_payload(["A"]))

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    with caplog.at_level(logging.INFO, logger="app.providers.gemini"):
        await GeminiProvider().generate_lesson_theory("BE", "Junior", "Bài", ["A"], None)

    lines = _lines(caplog)
    assert len(lines) == 2
    assert "finish_reason='MAX_TOKENS'" in lines[0] and "defects=1" in lines[0]


def test_chan_doan_co_thoughts_token_count():
    out = _generation_diagnostics(_resp({}, thoughts=4321, candidates=17))
    assert "thoughts_token_count=4321" in out
    assert "candidates_token_count=17" in out


def test_chan_doan_thieu_thoughts_thi_in_None_khong_raise():
    meta = type("M", (), {"candidates_token_count": 17})()
    cand = type("C", (), {"finish_reason": "STOP"})()
    resp = type("R", (), {"text": "{}", "candidates": [cand], "usage_metadata": meta})()
    assert "thoughts_token_count=None" in _generation_diagnostics(resp)
