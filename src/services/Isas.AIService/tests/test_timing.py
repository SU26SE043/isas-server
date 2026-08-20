"""Đo tách chặng (`app/timing.py`).

Đây là công cụ QUAN SÁT, nên bất biến quan trọng nhất không phải "đo đúng bao nhiêu giây" mà là
"không làm hỏng thứ nó quan sát": không rò số liệu giữa request, không nuốt exception, và không đổi
chữ ký hàm mà FastAPI đọc.
"""

import asyncio
import inspect
import logging

import pytest

from app import timing
from app.config import settings


@pytest.fixture(autouse=True)
def _bat_do(monkeypatch):
    """Mặc định bật đo cho mọi test ở đây; ca "tắt cờ" tự ghi đè lại."""
    monkeypatch.setattr(settings, "timing_log_enabled", True)


def test_record_ngoai_request_la_no_op():
    """Không có request nào đang đo ⇒ `record` im lặng bỏ qua, KHÔNG ném.

    Đây là điều kiện để gọi `timing.record` từ code dùng chung (provider, registry, usage) mà không
    bắt mọi call site phải mở một lượt đo — và để test cũ không cần biết module này tồn tại.
    """
    timing.record("bat_ky", 1.0)
    with timing.stage("cung_khong_sao"):
        pass


def test_stage_ghi_vao_bang_cua_request():
    with timing.request_timing("thu") as bag:
        with timing.stage("a"):
            pass
    assert "a" in bag


def test_stage_cong_don_khi_chay_nhieu_lan():
    """CỘNG DỒN chứ không ghi đè — vòng retry của `_generate` chạy cùng một chặng nhiều lượt."""
    with timing.request_timing("thu") as bag:
        timing.record("gemini", 1.0)
        timing.record("gemini", 2.5)
    assert bag["gemini"] == pytest.approx(3.5)


def test_stage_van_tinh_khi_khoi_lenh_nem_loi():
    """Ca HỎNG thường là ca CHẬM (timeout, retry). Bỏ nó khỏi bảng đo thì chỉ còn ca đẹp."""
    with timing.request_timing("thu") as bag:
        with pytest.raises(ValueError):
            with timing.stage("no"):
                raise ValueError("bùm")
    assert "no" in bag


async def test_context_xuyen_duoc_sang_thread():
    """`asyncio.to_thread` sao chép context ⇒ chặng nằm trong thread ASR vẫn ghi đúng bảng.

    Không có tính chất này thì `usage_post_asr` (POST đồng bộ bên trong thread chép lời) không đo
    được, mà đó đúng là một trong ba chặng ẩn cần nhìn.
    """
    def trong_thread():
        with timing.stage("trong_thread"):
            pass

    with timing.request_timing("thu") as bag:
        await asyncio.to_thread(trong_thread)
    assert "trong_thread" in bag


async def test_khong_ro_so_lieu_giua_hai_request_chay_song_song():
    """Hai request đồng thời phải có hai bảng RIÊNG — đây là lý do mặc định của ContextVar là None
    chứ không phải một dict dùng chung."""
    async def mot_luot(ten: str):
        with timing.request_timing(ten) as bag:
            timing.record(ten, 1.0)
            await asyncio.sleep(0)
            return dict(bag)

    a, b = await asyncio.gather(mot_luot("a"), mot_luot("b"))
    assert a == {"a": 1.0}
    assert b == {"b": 1.0}


def test_in_dung_mot_dong_moi_request(caplog):
    """Một dòng cuối request, không phải mỗi chặng một dòng — production đọc bằng `docker logs`."""
    with caplog.at_level(logging.INFO, logger="app.timing"):
        with timing.request_timing("decide-next"):
            timing.record("asr", 3.0)
            timing.record("gemini", 2.0)

    dong = [r for r in caplog.records if r.name == "app.timing"]
    assert len(dong) == 1
    text = dong[0].getMessage()
    assert "decide-next" in text and "asr=3.00" in text and "gemini=2.00" in text
    assert "total=" in text


def test_tat_co_thi_khong_log_va_khong_do(caplog, monkeypatch):
    """Tắt cờ ⇒ không dòng log NÀO, và `record` bên trong cũng thành no-op (chi phí về gần 0)."""
    monkeypatch.setattr(settings, "timing_log_enabled", False)
    with caplog.at_level(logging.INFO, logger="app.timing"):
        with timing.request_timing("decide-next") as bag:
            timing.record("asr", 3.0)

    assert bag is None
    assert [r for r in caplog.records if r.name == "app.timing"] == []


async def test_timed_request_giu_nguyen_chu_ky():
    """FastAPI dựng validation/dependency từ `inspect.signature`, mà nó đi theo `__wrapped__`.

    Mất tính chất này thì endpoint vẫn "chạy" nhưng nhận sai tham số — hỏng theo kiểu không test
    nào của endpoint kêu, vì chúng gọi thẳng hàm chứ không qua FastAPI.
    """
    @timing.timed_request("thu")
    async def handler(req: int, x_internal_token: str | None = None) -> int:
        return req + 1

    assert list(inspect.signature(handler).parameters) == ["req", "x_internal_token"]
    assert handler.__name__ == "handler"
    assert await handler(1) == 2


async def test_timed_request_khong_nuot_exception():
    @timing.timed_request("thu")
    async def handler():
        raise RuntimeError("bùm")

    with pytest.raises(RuntimeError):
        await handler()
