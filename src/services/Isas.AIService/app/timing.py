"""Đo tách chặng cho MỘT request — trả lời "thời gian đi đâu mất", không phải "có chậm không".

Vì sao cần: đo trên production 2026-08-20 cho thấy độ trễ sinh câu đào sâu (p50 6,5s · p90 27,6s)
gần như KHÔNG tăng theo độ dài audio — audio dài gấp 3 mà độ trễ chỉ tăng 1,3× ⇒ phần lớn là chi
phí CỐ ĐỊNH. Nhưng `/decide-next` có 10 chặng và trước file này AIService **không có một dòng đo
thời gian nào** (`time.perf_counter` không xuất hiện ở đâu trong `app/`), nên không cách nào biết
chặng nào ăn phần đó. Đoán rồi vá là cách hỏng: đã ba lần trong dự án này một giả thuyết đọc từ
bảng thống kê gộp bị thí nghiệm có đối chứng bác bỏ.

Vì sao dùng ``ContextVar`` chứ không truyền tham số: ba chặng đáng ngờ nhất nằm SÂU trong provider
chứ không ở tầng handler — ``prompt_registry.refresh_if_stale()`` (HTTP ẩn khi cache hết hạn),
``report_usage`` (POST tới Payment sink nằm THẲNG trong critical path của mọi lượt Gemini), và
``report_blocking`` (``asyncio.run`` POST đồng bộ bên trong thread ASR). Nhét một tham số "timer"
xuyên qua từng chữ ký để tới được chúng là đổi hợp đồng của nửa codebase cho một việc chỉ để quan
sát. ``ContextVar`` đo được cả ba mà không đổi một chữ ký nào, và ``asyncio.to_thread`` sao chép
context sang thread nên chặng nằm trong thread ASR cũng ghi được.

Ngoài request có gắn :func:`request_timing`, :func:`record` là no-op — nên gọi nó từ code dùng chung
(provider, registry, usage) KHÔNG bắt endpoint nào cũng phải bật đo, và test cũ không cần biết file
này tồn tại.
"""

from __future__ import annotations

import functools
import logging
import time
from contextlib import contextmanager
from contextvars import ContextVar
from typing import Any, Callable, Iterator

from app.config import settings

_logger = logging.getLogger(__name__)

# None = không có request nào đang đo ⇒ `record` no-op. Không dùng dict rỗng làm mặc định: mặc định
# khả biến dùng chung giữa mọi context là đúng cái bẫy làm số liệu của request này rò sang request kia.
_stages: ContextVar[dict[str, float] | None] = ContextVar("_stage_timings", default=None)


def record(stage: str, seconds: float) -> None:
    """Cộng dồn ``seconds`` vào chặng ``stage`` của request hiện tại; no-op nếu không đang đo.

    CỘNG DỒN chứ không ghi đè: một chặng có thể chạy nhiều lần trong cùng request (vòng retry của
    ``_generate``, hai lượt ``report_usage``). Ghi đè sẽ báo cáo lượt cuối và giấu mất phần đắt nhất.
    """
    bag = _stages.get()
    if bag is None:
        return
    bag[stage] = bag.get(stage, 0.0) + seconds


@contextmanager
def stage(name: str) -> Iterator[None]:
    """Đo một khối lệnh và cộng vào chặng ``name``.

    ``finally`` chứ không phải sau ``yield``: chặng ném lỗi vẫn phải được tính, vì ca hỏng thường
    là ca CHẬM (timeout, retry) — bỏ nó đi thì bảng đo chỉ còn ca đẹp.
    """
    start = time.perf_counter()
    try:
        yield
    finally:
        record(name, time.perf_counter() - start)


@contextmanager
def request_timing(label: str) -> Iterator[dict[str, float] | None]:
    """Mở một lượt đo cho cả request và in ĐÚNG MỘT dòng tổng kết khi xong.

    Một dòng cuối request, không phải mỗi chặng một dòng: production đọc log bằng ``docker logs``,
    một dòng thì grep/parse được và không nhân khối lượng log lên 10 lần.

    Tắt bằng ``TIMING_LOG_ENABLED=false`` — khi tắt, ``_stages`` không được set nên mọi ``record``
    bên trong cũng thành no-op, tức chi phí đo về gần đúng 0 chứ không phải "vẫn đo rồi vứt".
    """
    if not settings.timing_log_enabled:
        yield None
        return

    bag: dict[str, float] = {}
    token = _stages.set(bag)
    start = time.perf_counter()
    try:
        yield bag
    finally:
        _stages.reset(token)
        total = time.perf_counter() - start
        # `%`-lazy theo đúng lối các log sẵn có trong `main.py`: chuỗi chỉ được dựng khi log thật sự
        # được phát, nên bật đo mà tắt INFO thì không tốn gì.
        _logger.info(
            "[⏱] %s total=%.2f %s",
            label,
            total,
            " ".join(f"{k}={v:.2f}" for k, v in bag.items()),
        )


def timed_request(label: str) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
    """Decorator bọc một endpoint async trong :func:`request_timing`.

    Dùng decorator thay vì bọc thân hàm bằng ``with``: thân handler dài và có nhiều nhánh ``return``
    sớm, thụt lề lại toàn bộ chỉ để quan sát là đổi ~90 dòng cho một việc không đổi hành vi — diff
    kiểu đó che mất thay đổi thật lúc review.

    ``functools.wraps`` gán ``__wrapped__``, mà ``inspect.signature`` đi theo nó ⇒ FastAPI vẫn đọc
    đúng chữ ký gốc để dựng dependency/validation. Vì vậy decorator này PHẢI đặt DƯỚI
    ``@router.post(...)``: route phải đăng ký bản đã bọc, không phải bản gốc.
    """

    def decorate(fn: Callable[..., Any]) -> Callable[..., Any]:
        @functools.wraps(fn)
        async def wrapper(*args: Any, **kwargs: Any) -> Any:
            with request_timing(label):
                return await fn(*args, **kwargs)

        return wrapper

    return decorate
