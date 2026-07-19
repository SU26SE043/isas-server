# tests/test_worker_dlq.py — AI2: DLX/DLQ topology + regression quyết định ack/nack.
#   KHÔNG có broker sống trong CI → test topology bằng AsyncMock channel (assert khai
#   đúng DLX/DLQ/bind + queue chính mang args dead-letter). Việc "nack → route sang DLQ"
#   là của broker (không unit-test được), nên process_message chỉ test quyết định
#   ack/nack — regression guard cho nhánh PermanentError sau khi thêm DLX.
import json
from unittest.mock import AsyncMock, MagicMock

import pytest

from app import worker
from app.transcriber import TranscriptionResult
from app.providers.gemini import ScoreOutcome
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
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed",
                        MagicMock(return_value=TranscriptionResult(text=transcript)))
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


# ── Adaptive: transcript đính kèm job → BỎ QUA Whisper (single-source) ───────
@pytest.mark.asyncio
async def test_process_message_skips_whisper_when_transcript_present(monkeypatch):
    """Job mang `transcript` (Interview đã transcribe đồng bộ khi decide-next) → worker
    KHÔNG tải audio, KHÔNG gọi Whisper; chấm thẳng transcript đó rồi callback + ack.
    Tiết kiệm N lần Whisper (self-consistency E10)."""
    download = MagicMock()
    transcribe = MagicMock(return_value=TranscriptionResult(text="KHÔNG NÊN DÙNG"))
    score = AsyncMock(return_value=ScoreOutcome(
        scores=[{"criterionId": "c1", "score": 3.0,
                 "levelMatched": 3, "reasoning": "x"}],
        sample_answer="Câu trả lời mẫu."))
    post_callback = AsyncMock()
    monkeypatch.setattr(worker.s3_client, "download_fileobj", download)
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed", transcribe)
    monkeypatch.setattr(worker.provider, "score", score)
    monkeypatch.setattr(worker, "post_callback", post_callback)

    job_metrics = {"speechRateWpm": 210.0, "fillerCount": 4, "longestPauseSec": 1.8}
    message = _fake_message({
        "answerId": "answer-3",
        "audioObjectKey": "recordings/a3.webm",
        "transcript": "Câu trả lời đã transcribe sẵn.",
        "deliveryMetrics": job_metrics,     # F11 — đo sẵn ở /decide-next
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    })

    await worker.process_message(message)

    download.assert_not_called()          # KHÔNG tải audio
    transcribe.assert_not_called()        # KHÔNG Whisper
    # transcript có sẵn được đưa thẳng vào score() + callback.
    assert score.call_args.kwargs["transcript"] == "Câu trả lời đã transcribe sẵn."
    assert post_callback.await_args.args[0]["transcript"] == "Câu trả lời đã transcribe sẵn."
    message.ack.assert_awaited_once()
    message.nack.assert_not_called()

    # F11 — CHỖ DỄ HỎNG ÂM THẦM NHẤT: worker bỏ Whisper ở đường này, nên nếu không nhận chỉ số
    # từ job thì buổi THÍCH ỨNG vĩnh viễn không có chỉ số trong khi buổi TĨNH vẫn có — không lỗi,
    # không log, chỉ là tính năng chết một nửa. Khoá cả hai đầu (vào bộ chấm + lên .NET).
    assert score.call_args.kwargs["delivery"] == job_metrics
    assert post_callback.await_args.args[0]["deliveryMetrics"] == job_metrics


@pytest.mark.asyncio
async def test_process_message_transcript_khong_kem_chi_so_van_cham_duoc(monkeypatch):
    """F11 degrade — job CŨ (Interview chưa deploy bản F11) mang transcript nhưng KHÔNG mang
    chỉ số. Worker phải chấm bình thường với ``delivery=None`` (prompt nói "chưa đo được"),
    TUYỆT ĐỐI không transcribe lại (mất trọn cái lợi bỏ Whisper) và không làm answer Failed
    (PAY-13: Failed = người luyện mất credit vì một tính năng phụ)."""
    transcribe = MagicMock(return_value=TranscriptionResult(text="KHÔNG NÊN DÙNG"))
    score = AsyncMock(return_value=ScoreOutcome(
        scores=[{"criterionId": "c1", "score": 3.0, "levelMatched": 3, "reasoning": "x"}],
        sample_answer=None))
    post_callback = AsyncMock()
    monkeypatch.setattr(worker.s3_client, "download_fileobj", MagicMock())
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed", transcribe)
    monkeypatch.setattr(worker.provider, "score", score)
    monkeypatch.setattr(worker, "post_callback", post_callback)

    message = _fake_message({
        "answerId": "answer-old",
        "audioObjectKey": "recordings/old.webm",
        "transcript": "Job cũ không có deliveryMetrics.",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    })

    await worker.process_message(message)

    transcribe.assert_not_called()
    assert score.call_args.kwargs["delivery"] is None
    assert post_callback.await_args.args[0]["deliveryMetrics"] is None
    message.ack.assert_awaited_once()


@pytest.mark.asyncio
async def test_process_message_transcribes_when_no_transcript(monkeypatch):
    """Regression: KHÔNG có transcript trong job (đường cũ) → vẫn tải audio + Whisper.

    F11 gộp thêm vế ĐƯỜNG TĨNH: worker tự transcribe thì phải TỰ đo chỉ số cách nói và
    chuyển tiếp cả xuống ``score()`` lẫn lên callback .NET."""
    from app.fluency import Segment, compute_delivery_metrics

    download = MagicMock()
    metrics = compute_delivery_metrics(
        "transcript từ whisper", [Segment(0.0, 2.0, "transcript từ whisper")], 4.0)
    transcribe = MagicMock(return_value=TranscriptionResult(
        text="transcript từ whisper", metrics=metrics))
    score = AsyncMock(return_value=ScoreOutcome(
        scores=[{"criterionId": "c1", "score": 3.0,
                 "levelMatched": 3, "reasoning": "x"}],
        sample_answer="Câu trả lời mẫu."))
    post_callback = AsyncMock()
    monkeypatch.setattr(worker.s3_client, "download_fileobj", download)
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed", transcribe)
    monkeypatch.setattr(worker.provider, "score", score)
    monkeypatch.setattr(worker, "post_callback", post_callback)

    message = _fake_message({
        "answerId": "answer-4",
        "audioObjectKey": "recordings/a4.webm",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    })

    await worker.process_message(message)

    download.assert_called_once()
    transcribe.assert_called_once()
    assert score.call_args.kwargs["transcript"] == "transcript từ whisper"
    message.ack.assert_awaited_once()

    # F11 — số đo phải tới CẢ bộ chấm (để chấm độ trôi chảy) LẪN .NET (để hiện cho người luyện).
    assert score.call_args.kwargs["delivery"] == metrics.to_dict()
    assert post_callback.await_args.args[0]["deliveryMetrics"] == metrics.to_dict()
