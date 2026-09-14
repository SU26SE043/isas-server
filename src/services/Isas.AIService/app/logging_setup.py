"""Bật log cho cây logger ``app.*`` dưới uvicorn / worker.

Vì sao cần file này: uvicorn CHỈ cấu hình logger ``uvicorn.*``; logger gốc (root) giữ mức WARNING và
không có handler nào. Mọi ``logging.getLogger(__name__)`` trong ``app/`` vì thế ở mức INFO là **im
lặng tuyệt đối** — đo trên ``aiapi-main`` 2026-09-14: 24 giờ log không có MỘT dòng nào ngoài access
log của uvicorn, dù code có ``logger.info('Bài giảng "%s" bị trả lại')``, ``[⏱]`` của ``app.timing`` và
dòng usage F22. Hệ quả cụ thể: tỉ lệ bài giảng bị rubric trả lại (mỗi lần = một lượt Gemini ~50s nằm
trong request của người học) **không đo được**, và một lượt ``/generate-lesson-theory`` hỏng chỉ lộ ra
dưới dạng 502 không kèm lý do. WARNING trở lên thì thoát được nhờ ``logging.lastResort`` — đó là lý do
những dòng cảnh báo vẫn thấy được trước đây, còn INFO thì không.

Cách làm — CỐ Ý hẹp:
- Chỉ đụng logger ``app`` (không đụng root, ``uvicorn``, ``httpx``, ``google``): bật root ở INFO là
  kéo theo log của mọi thư viện.
- GIỮ ``propagate=True``: pytest ``caplog`` treo handler ở root; 6 file test đang đọc ``caplog.records``
  của ``app.providers.gemini``/``app.timing`` — tắt propagate là làm câm những test đó mà không đỏ.
- Idempotent theo TÊN handler: ``main.py`` và ``worker.py`` đều gọi, và test có thể gọi nhiều lần;
  gắn thêm handler mỗi lần là mỗi dòng log in ra n lần.
"""

from __future__ import annotations

import logging

APP_LOGGER_NAME = "app"
HANDLER_NAME = "isas-app"


def configure(level: str = "INFO") -> None:
    """Gắn đúng MỘT ``StreamHandler`` (stderr — ``docker logs`` gom) lên logger ``app``."""
    app_logger = logging.getLogger(APP_LOGGER_NAME)
    if not any(getattr(h, "name", None) == HANDLER_NAME for h in app_logger.handlers):
        handler = logging.StreamHandler()
        handler.set_name(HANDLER_NAME)
        handler.setFormatter(logging.Formatter("%(levelname)s %(name)s: %(message)s"))
        app_logger.addHandler(handler)
    app_logger.setLevel(str(level or "INFO").upper())
    app_logger.propagate = True
