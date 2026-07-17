# tests/test_worker_dlq.py — AI2: DLX/DLQ topology + regression quyết định ack/nack.
#   KHÔNG có broker sống trong CI → test topology bằng AsyncMock channel (assert khai
#   đúng DLX/DLQ/bind + queue chính mang args dead-letter). Việc "nack → route sang DLQ"
#   là của broker (không unit-test được), nên process_message chỉ test quyết định
#   ack/nack — regression guard cho nhánh PermanentError sau khi thêm DLX.
import json
from unittest.mock import AsyncMock, MagicMock

import pytest

from app import worker
from app.config import settings


# ── Topology: DLX + DLQ + bind + main queue mang args dead-letter ────────────
@pytest.mark.asyncio
async def test_declare_topology_sets_up_dlx_dlq_and_main_queue():
    dlx = MagicMock(name="dlx")
    dead_queue = MagicMock(name="dead_queue")
    dead_queue.bind = AsyncMock()
    main_queue = MagicMock(name="main_queue")

    channel = MagicMock(name="channel")
    channel.declare_exchange = AsyncMock(return_value=dlx)
    # declare_queue gọi 2 lần: (1) DLQ, (2) queue chính.
    channel.declare_queue = AsyncMock(side_effect=[dead_queue, main_queue])

    result = await worker.declare_topology(channel)

    # DLX khai là direct + durable.
    channel.declare_exchange.assert_awaited_once()
    ex_args, ex_kwargs = channel.declare_exchange.call_args
    assert settings.dlx_name in ex_args
    assert ex_kwargs.get("durable") is True

    # 2 lần declare_queue theo đúng thứ tự: DLQ trước, queue chính sau.
    assert channel.declare_queue.await_count == 2
    first_call, second_call = channel.declare_queue.call_args_list
    assert settings.dead_queue_name in first_call.args
    assert settings.queue_name in second_call.args
    assert first_call.kwargs.get("durable") is True
    assert second_call.kwargs.get("durable") is True

    # DLQ bind vào DLX bằng routing key dead.
    dead_queue.bind.assert_awaited_once_with(dlx, routing_key=settings.dead_routing_key)

    # Queue chính MANG args dead-letter trỏ về DLX (nack(requeue=False) → DLX → DLQ).
    main_args = second_call.kwargs["arguments"]
    assert main_args["x-dead-letter-exchange"] == settings.dlx_name
    assert main_args["x-dead-letter-routing-key"] == settings.dead_routing_key

    # Trả về queue chính để consume().
    assert result is main_queue


def test_topology_names_match_dotnet_publisher_contract():
    """Args queue chính PHẢI trùng y hệt ScoringJobPublisher.cs (.NET) — lệch → RabbitMQ 406.
    Khoá hằng số ở đây để đổi 1 bên mà quên bên kia bị test bắt."""
    assert settings.dlx_name == "scoring_pipeline_dlx"
    assert settings.dead_routing_key == "scoring_dead"
    assert settings.dead_queue_name == "scoring_pipeline_dead_queue"


# ── process_message: nhánh PermanentError giữ nguyên quyết định ack/nack ─────
def _fake_message(body: dict):
    message = MagicMock(name="message")
    message.body = json.dumps(body).encode()
    message.ack = AsyncMock()
    message.nack = AsyncMock()
    return message


def _patch_pipeline(monkeypatch, *, transcript="một transcript hợp lệ", post_failed):
    """Mock S3 tải + transcribe OK để tới được bước score(); score() luôn raise ValueError
    (→ PermanentError). post_failed do caller quyết OK/raise để test 2 nhánh ack vs nack."""
    monkeypatch.setattr(worker.s3_client, "download_fileobj", MagicMock())
    monkeypatch.setattr(worker.transcriber, "transcribe", MagicMock(return_value=transcript))
    monkeypatch.setattr(worker.provider, "score",
                        AsyncMock(side_effect=ValueError("LLM output không hợp lệ")))
    monkeypatch.setattr(worker, "post_failed", post_failed)


@pytest.mark.asyncio
async def test_process_message_permanent_error_acks_after_report(monkeypatch):
    """score() ValueError → PermanentError → post_failed OK → message.ack (KHÔNG nack).
    Regression: đường 'đã báo .NET Failed' vẫn ACK, KHÔNG dead-letter oan."""
    post_failed = AsyncMock()
    _patch_pipeline(monkeypatch, post_failed=post_failed)

    message = _fake_message({
        "answerId": "answer-1",
        "audioObjectKey": "recordings/a1.webm",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    })

    await worker.process_message(message)

    post_failed.assert_awaited_once()
    message.ack.assert_awaited_once()
    message.nack.assert_not_called()


@pytest.mark.asyncio
async def test_process_message_nacks_to_dlq_when_report_also_fails(monkeypatch):
    """PermanentError + post_failed CŨNG fail (mạng) → nack(requeue=False) → message rơi
    sang DLX/DLQ (AI2). Đây là ca DLQ có giá trị: không thì message mất hẳn."""
    _patch_pipeline(monkeypatch,
                    post_failed=AsyncMock(side_effect=RuntimeError("mạng lỗi khi báo Failed")))

    message = _fake_message({
        "answerId": "answer-2",
        "audioObjectKey": "recordings/a2.webm",
        "questionContent": "Q?",
        "criteria": [],
    })

    await worker.process_message(message)

    message.nack.assert_awaited_once_with(requeue=False)
    message.ack.assert_not_called()
