# tests/test_retry.py — AI3: score() retry lỗi parse chớp nhoáng trước khi báo Failed.
#   score() raise ValueError khi LLM trả output không parse/không hợp lệ. Trước AI3
#   worker báo Failed NGAY; nay thử lại tối đa `score_max_attempts` lần. Test không có
#   broker/Gemini thật — mock pipeline (S3 tải + transcribe + score + callback) như
#   test_worker_dlq.py, chỉ đổi score.side_effect để dựng 2 kịch bản.
import json
from unittest.mock import AsyncMock, MagicMock

import pytest

from app import worker
from app.config import settings


def _fake_message(body: dict):
    message = MagicMock(name="message")
    message.body = json.dumps(body).encode()
    message.ack = AsyncMock()
    message.nack = AsyncMock()
    return message


def _patch_pipeline(monkeypatch, *, score, post_callback, post_failed,
                    transcript="một transcript hợp lệ"):
    """Mock S3 tải + transcribe OK để tới bước score(). Caller quyết score.side_effect
    + hai callback (post_callback = success, post_failed = báo Failed)."""
    monkeypatch.setattr(worker.s3_client, "download_fileobj", MagicMock())
    monkeypatch.setattr(worker.transcriber, "transcribe", MagicMock(return_value=transcript))
    monkeypatch.setattr(worker.provider, "score", score)
    monkeypatch.setattr(worker, "post_callback", post_callback)
    monkeypatch.setattr(worker, "post_failed", post_failed)


# Kết quả chấm hợp lệ (E9 shape) khi score() thành công.
_VALID_SCORES = [{"criterionId": "c1", "score": 3.0, "levelMatched": 3, "reasoning": "ok"}]


def test_score_max_attempts_default_is_three():
    """Mặc định 3 = 1 lần đầu + 2 retry (khoá hằng số config AI3)."""
    assert settings.score_max_attempts == 3


@pytest.mark.asyncio
async def test_score_valueerror_once_then_success_reports_scored(monkeypatch):
    """AI3: score() ValueError 1 lần rồi trả kết quả hợp lệ → answer Scored:
    post_callback ĐƯỢC gọi, post_failed KHÔNG, message.ack (không nack/không Failed)."""
    score = AsyncMock(side_effect=[ValueError("JSON cụt"), _VALID_SCORES])
    post_callback = AsyncMock()
    post_failed = AsyncMock()
    _patch_pipeline(monkeypatch, score=score,
                    post_callback=post_callback, post_failed=post_failed)

    message = _fake_message({
        "answerId": "answer-retry-ok",
        "audioObjectKey": "recordings/a1.webm",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    })

    await worker.process_message(message)

    assert score.await_count == 2            # thử lại đúng 1 lần rồi thành công
    post_callback.assert_awaited_once()       # đường Scored
    post_failed.assert_not_called()           # KHÔNG báo Failed
    message.ack.assert_awaited_once()
    message.nack.assert_not_called()


@pytest.mark.asyncio
async def test_score_valueerror_every_attempt_reports_failed(monkeypatch):
    """AI3: score() ValueError MỌI lần → sau score_max_attempts lần → PermanentError →
    post_failed ĐƯỢC gọi (answer Failed), post_callback KHÔNG, message.ack."""
    score = AsyncMock(side_effect=ValueError("LLM output không hợp lệ"))
    post_callback = AsyncMock()
    post_failed = AsyncMock()
    _patch_pipeline(monkeypatch, score=score,
                    post_callback=post_callback, post_failed=post_failed)

    message = _fake_message({
        "answerId": "answer-retry-fail",
        "audioObjectKey": "recordings/a2.webm",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    })

    await worker.process_message(message)

    assert score.await_count == settings.score_max_attempts  # đã cạn số lần thử
    post_failed.assert_awaited_once()          # báo .NET Failed
    post_callback.assert_not_called()          # KHÔNG có đường Scored
    message.ack.assert_awaited_once()          # báo Failed OK → ack (không dead-letter oan)
    message.nack.assert_not_called()
