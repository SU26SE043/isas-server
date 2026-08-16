# tests/test_no_speech_wire.py — bản ghi im lặng phải đi tới .NET dưới đúng NHÃN của nó.
#
# Cổng im lặng (test_silence_gate.py) chỉ chặn ở tầng transcriber. File này khoá hai đường DÂY
# mang tín hiệu đó sang .NET, vì đó mới là chỗ quyết định người luyện nhìn thấy gì:
#   • worker (đường tĩnh, qua RabbitMQ) → callback /failed kèm `noSpeech` → .NET đánh `Skipped`;
#   • /decide-next (đường thích ứng, ĐỒNG BỘ trong request upload) → `rejectReason`.
#
# Cả hai đều là hợp đồng dây bằng TÊN KHOÁ, tức lớp lỗi đã cắn repo này ba lần (`focusCriteria`
# bị pydantic nuốt · `metricsVersion` rụng ở schema response · `adaptiveMaxQuestions` vs
# `maxQuestions`): đổi tên KHÔNG ném lỗi, chỉ lặng lẽ quay về hành vi cũ.
import json
from unittest.mock import AsyncMock, MagicMock

import pytest

from app import worker
from app.transcriber import JUNK_TRANSCRIPT, NO_SPEECH, TranscriptionResult


def _fake_message(body: dict):
    message = MagicMock(name="message")
    message.body = json.dumps(body).encode()
    message.ack = AsyncMock()
    message.nack = AsyncMock()
    return message


def _job(answer_id="answer-1"):
    return {
        "answerId": answer_id,
        "audioObjectKey": "recordings/a1.m4a",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    }


def _patch(monkeypatch, *, reject_reason, post_failed, score=None):
    monkeypatch.setattr(worker.s3_client, "download_fileobj", MagicMock())
    monkeypatch.setattr(
        worker.transcriber, "transcribe_detailed",
        MagicMock(return_value=TranscriptionResult(text="", reject_reason=reject_reason)))
    monkeypatch.setattr(worker.provider, "score", score or AsyncMock())
    monkeypatch.setattr(worker, "post_failed", post_failed)


@pytest.mark.asyncio
async def test_worker_im_lang_bao_noSpeech_va_khong_cham(monkeypatch):
    """Im lặng → báo .NET kèm cờ `no_speech` và TUYỆT ĐỐI không gọi bộ chấm.

    Vế "không gọi score()" là phần đáng tiền: trước bản vá, đúng ca này đã tốn một lượt Gemini
    để chấm một câu quảng cáo do máy bịa ra.
    """
    post_failed = AsyncMock()
    score = AsyncMock()
    _patch(monkeypatch, reject_reason=NO_SPEECH, post_failed=post_failed, score=score)
    message = _fake_message(_job())

    await worker.process_message(message)

    score.assert_not_awaited()
    post_failed.assert_awaited_once()
    assert post_failed.await_args.kwargs["no_speech"] is True
    message.ack.assert_awaited_once()


@pytest.mark.asyncio
async def test_worker_ban_chep_rac_van_la_Failed_khong_phai_Skipped(monkeypatch):
    """Rác ≠ im lặng: rác là hỏng hóc KỸ THUẬT (cả hai engine đều ra chuỗi máy sinh), còn im
    lặng là chuyện của người trả lời. Gộp nhãn là nói dối người luyện một trong hai chiều."""
    post_failed = AsyncMock()
    _patch(monkeypatch, reject_reason=JUNK_TRANSCRIPT, post_failed=post_failed)
    message = _fake_message(_job("answer-2"))

    await worker.process_message(message)

    post_failed.assert_awaited_once()
    assert post_failed.await_args.kwargs["no_speech"] is False


@pytest.mark.asyncio
async def test_post_failed_gui_dung_khoa_camelCase(monkeypatch):
    """Khoá phải là `noSpeech` — khớp `AnswerFailedCallbackRequest.NoSpeech` (.NET).

    Bind hụt ở .NET không ném lỗi: answer chỉ lặng lẽ rơi về `Failed` như trước.
    """
    captured = {}

    class _Resp:
        status = 204

        async def text(self):
            return ""

        async def __aenter__(self):
            return self

        async def __aexit__(self, *a):
            return False

    class _Session:
        def post(self, url, json=None, headers=None):
            captured["url"] = url
            captured["json"] = json
            return _Resp()

        async def __aenter__(self):
            return self

        async def __aexit__(self, *a):
            return False

    monkeypatch.setattr(worker.aiohttp, "ClientSession", lambda *a, **k: _Session())

    await worker.post_failed("answer-3", "Bản ghi không có tiếng nói (VAD)", no_speech=True)

    assert captured["json"]["noSpeech"] is True
    assert "reason" in captured["json"]


def test_decide_next_im_lang_tra_rejectReason_va_khong_goi_gemini(monkeypatch):
    """/decide-next: im lặng → trả thẳng `rejectReason`, KHÔNG hỏi Gemini câu kế.

    `action="end"` ở đây nghĩa là "lượt này không sinh câu kế", KHÔNG phải "buổi đã xong" —
    .NET bản mới đọc `rejectReason` và thoát trước khi nhìn tới action.
    """
    from fastapi.testclient import TestClient

    from app import main as main_mod
    from app.config import settings as app_settings

    monkeypatch.setattr(app_settings, "internal_token", "tok")
    monkeypatch.setattr(main_mod.storage, "get_object_bytes", lambda key: b"\x00\x00")
    monkeypatch.setattr(
        main_mod.transcriber, "transcribe_detailed",
        MagicMock(return_value=TranscriptionResult(
            text="", reject_reason=NO_SPEECH, engine="local:small")))
    decide = AsyncMock()
    monkeypatch.setattr(main_mod.provider, "decide_next", decide)

    client = TestClient(main_mod.app)
    resp = client.post("/api/v1/decide-next",
                       headers={"X-Internal-Token": "tok"},
                       json={
                           "audioObjectKey": "recordings/a1.m4a",
                           "jobCategory": "BE",
                           "currentQuestion": "Q?",
                           "history": [],
                           "askedCount": 1,
                           "followUpCount": 0,
                           "maxQuestions": 6,
                           "maxFollowUps": 3,
                           "criteria": [],
                       })

    assert resp.status_code == 200
    body = resp.json()
    assert body["rejectReason"] == NO_SPEECH
    assert body["action"] == "end"
    assert body.get("nextQuestion") is None
    assert body.get("transcript") is None, "KHÔNG trả chuỗi rác ra ngoài"
    decide.assert_not_awaited()
