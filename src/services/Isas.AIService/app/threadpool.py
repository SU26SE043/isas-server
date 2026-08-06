"""Trần thread cho công việc CHẶN (`asyncio.to_thread`).

Mọi việc nặng của service đi qua executor mặc định của event loop: đọc S3, giải mã audio, và
lời gọi chép lời ĐỒNG BỘ (httpx blocking trong `transcribe_openai`). Cỡ mặc định là
``min(32, cpu_count + 4)`` (CPython ``Lib/concurrent/futures/thread.py``) ⇒ **12** trên server
8 core — tức 12 là trần số ``/decide-next`` chạy song song, dù mạng và nhà cung cấp còn dư.

Tách ra module riêng để phần QUYẾT ĐỊNH (``resolve_executor``) là hàm thuần, test được mà không
phải chọc vào ``loop._default_executor``.
"""
import logging
from concurrent.futures import ThreadPoolExecutor

logger = logging.getLogger(__name__)


def resolve_executor(max_workers: int) -> ThreadPoolExecutor | None:
    """``None`` = giữ nguyên mặc định asyncio.

    ``0`` (mặc định cấu hình) và mọi giá trị âm đều trả ``None``: rollout của repo này luôn
    ship ở trạng thái no-op rồi mới bật bằng env, và ``ThreadPoolExecutor(max_workers=0)``
    ném ``ValueError`` — biến một cấu hình sai thành container chết lúc khởi động.
    """
    if max_workers <= 0:
        return None
    return ThreadPoolExecutor(max_workers=max_workers, thread_name_prefix="isas-blocking")


def apply(loop, max_workers: int) -> ThreadPoolExecutor | None:
    """Đặt executor mặc định cho ``loop``. Trả executor đã đặt (hoặc ``None`` nếu giữ mặc định)."""
    executor = resolve_executor(max_workers)
    if executor is None:
        logger.info("Thread pool: giữ mặc định asyncio (min(32, cpu+4))")
        return None
    loop.set_default_executor(executor)
    logger.info("Thread pool: đặt trần %d thread cho công việc chặn", max_workers)
    return executor
