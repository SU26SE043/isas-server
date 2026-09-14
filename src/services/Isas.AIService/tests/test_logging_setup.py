# tests/test_logging_setup.py — app/logging_setup.py: bật INFO cho cây logger `app.*`.
#
# Vì sao có file này: uvicorn chỉ cấu hình logger `uvicorn.*`, root giữ WARNING không handler ⇒ mọi
# `logger.info` trong app/ IM LẶNG trên container (đo prod 2026-09-14: 24h log không một dòng nào
# ngoài access log). Ba tính chất phải giữ, mỗi cái một test + một mutation:
#   (1) bật được INFO cho `app.*`             — mutation: không setLevel → ĐỎ
#   (2) gọi nhiều lần không nhân đôi handler   — mutation: bỏ guard theo tên → ĐỎ
#   (3) GIỮ propagate=True                     — mutation: `propagate = False` → ĐỎ (và 6 file test
#       đang đọc caplog.records của app.* câm theo, đúng thứ (3) sinh ra để chặn)
import logging

from app import logging_setup


def _reset():
    app_logger = logging.getLogger(logging_setup.APP_LOGGER_NAME)
    for h in list(app_logger.handlers):
        if getattr(h, "name", None) == logging_setup.HANDLER_NAME:
            app_logger.removeHandler(h)
    app_logger.setLevel(logging.NOTSET)


def test_configure_bat_info_cho_cay_app():
    _reset()
    logging_setup.configure("INFO")
    assert logging.getLogger("app.providers.gemini").isEnabledFor(logging.INFO)
    assert any(getattr(h, "name", None) == logging_setup.HANDLER_NAME
               for h in logging.getLogger("app").handlers)


def test_configure_goi_hai_lan_khong_nhan_doi_handler():
    _reset()
    logging_setup.configure("INFO")
    logging_setup.configure("INFO")
    ours = [h for h in logging.getLogger("app").handlers
            if getattr(h, "name", None) == logging_setup.HANDLER_NAME]
    assert len(ours) == 1


def test_giu_propagate_de_caplog_van_bat_duoc():
    _reset()
    logging_setup.configure("INFO")
    assert logging.getLogger("app").propagate is True


def test_muc_log_doc_tu_tham_so_khong_phan_biet_hoa_thuong():
    _reset()
    logging_setup.configure("warning")
    assert logging.getLogger("app").level == logging.WARNING
    _reset()
    logging_setup.configure("INFO")
