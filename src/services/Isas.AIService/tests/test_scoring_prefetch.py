# tests/test_scoring_prefetch.py — trần ĐỒNG THỜI của worker chấm.
#
# Vì sao cần test riêng cho một con số config: giá trị cũ ghi CỨNG `1` ngay trong `main()`, mà
# `main()` mở kết nối RabbitMQ thật nên không unit-test được ⇒ ai sửa nhầm về 1 (hoặc bỏ quên
# `set_qos`) sẽ KHÔNG có test nào đỏ, trong khi hậu quả là throughput chấm tụt 4 lần MÀ KHÔNG
# CÓ LỖI NÀO — đúng loại hỏng âm thầm. Ở đây tách phần kiểm được (giá trị config + việc worker
# đọc config thay vì hằng số) ra khỏi phần cần broker.
#
# Không cần broker: dùng AsyncMock channel (mẫu `declare_topology` của AI2).
import inspect
import json
import threading
from unittest.mock import AsyncMock, MagicMock

import pytest

from app import worker
from app.config import Settings, settings
from app.providers.gemini import ScoreOutcome
from app.transcriber import TranscriptionResult


def test_prefetch_mac_dinh_cho_song_song():
    """Mặc định PHẢI > 1 — đây là toàn bộ lý do tồn tại của thay đổi này.

    Đo thật 2026-08-04: 1 lượt chấm 12,6s, 4 lượt song song 13,3s (chờ mạng Gemini chứ không
    CPU) ⇒ giữ 1 là vứt không throughput. Khoá cả cận trên: quá cao thì một đợt job đường TĨNH
    sẽ chạy ngần đó Whisper cùng lúc và bóp nghẹt CPU máy chạy worker.
    """
    assert settings.scoring_prefetch > 1, (
        "prefetch=1 làm worker chấm tuần tự — mất ~4x throughput mà không có lỗi nào báo")
    assert settings.scoring_prefetch <= 16, (
        "prefetch quá cao: mỗi message có thể kéo theo 1 lượt Whisper (đường tĩnh) + 1 lượt Gemini")


def test_prefetch_doc_tu_env():
    """Chỉnh được lúc chạy — hạ nhanh khi Gemini 429 mà không phải build lại image."""
    assert Settings(scoring_prefetch=3).scoring_prefetch == 3


def test_worker_dung_config_chu_khong_phai_hang_so():
    """Chốt rằng `main()` đọc `settings.scoring_prefetch`.

    Đọc thẳng mã nguồn vì `main()` mở kết nối thật nên không gọi được trong test. Mutation:
    ghi cứng lại `set_qos(prefetch_count=1)` → test này ĐỎ.
    """
    src = inspect.getsource(worker.main)
    assert "set_qos" in src, "worker.main() không còn đặt QoS — prefetch về mặc định của broker"
    assert "settings.scoring_prefetch" in src, (
        "worker.main() phải lấy prefetch từ config, không ghi cứng")


def test_scoring_va_cv_screening_tach_tran_rieng():
    """Hai luồng phải giữ trần RIÊNG.

    Gộp một con số là mất đúng lý do C14 tách channel: backlog audio (nặng, có Whisper) không
    được nghẽn sàng CV (nhẹ) và ngược lại.
    """
    assert hasattr(settings, "cv_screening_prefetch")
    assert (Settings(scoring_prefetch=7).cv_screening_prefetch
            == settings.cv_screening_prefetch), "đổi trần chấm không được kéo theo trần sàng CV"


# ── Trần đồng thời chỉ có nghĩa nếu KHÔNG có ai chặn event loop ───────────────
@pytest.mark.asyncio
async def test_tai_audio_khong_chan_event_loop(monkeypatch):
    """boto3 là BLOCKING — gọi thẳng trên event loop thì `prefetch=10` là con số trên giấy.

    Đây là mặt còn lại của chính test đầu file: nâng prefetch lên 10 rồi để một lượt tải S3 chạy
    đồng bộ trên loop thì suốt lượt tải đó, cả 9 coroutine kia ĐỨNG HÌNH — kể cả những lượt chỉ
    đang chờ mạng Gemini, tức là đúng phần song song mà prefetch=10 mua về. Hỏng câm tuyệt đối:
    không lỗi, không log, chỉ là throughput không bao giờ đạt con số đã cấu hình.

    Kiểm bằng thứ quan sát được thay vì đọc mã nguồn: ghi lại thread chạy `download_fileobj` và
    đòi nó KHÁC thread của event loop. `asyncio.to_thread` cho ra điều đó; gọi thẳng thì không.
    """
    loop_thread = threading.get_ident()
    download_thread: dict = {}

    def fake_download(bucket, key, fileobj):
        download_thread["ident"] = threading.get_ident()

    monkeypatch.setattr(worker.s3_client, "download_fileobj", MagicMock(side_effect=fake_download))
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed",
                        MagicMock(return_value=TranscriptionResult(text="một câu trả lời")))
    monkeypatch.setattr(worker.provider, "score", AsyncMock(return_value=ScoreOutcome(
        scores=[{"criterionId": "c1", "score": 3.0, "levelMatched": 3, "reasoning": "ok"}],
        sample_answer=None)))
    monkeypatch.setattr(worker, "post_callback", AsyncMock())
    monkeypatch.setattr(worker, "post_failed", AsyncMock())

    message = MagicMock(name="message")
    message.body = json.dumps({
        "answerId": "answer-to-thread",
        "audioObjectKey": "recordings/a1.webm",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    }).encode()
    message.ack = AsyncMock()
    message.nack = AsyncMock()

    await worker.process_message(message)

    message.ack.assert_awaited_once()   # đường Scored chạy trọn, không phải xanh vì bỏ qua tải
    assert download_thread.get("ident") is not None, "worker không hề gọi download_fileobj"
    assert download_thread["ident"] != loop_thread, (
        "download_fileobj (boto3, blocking) chạy THẲNG trên event loop — cả prefetch còn lại "
        "đứng hình suốt lượt tải; phải bọc asyncio.to_thread như /decide-next đang làm")
