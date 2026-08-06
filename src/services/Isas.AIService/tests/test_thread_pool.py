"""Trần thread cho công việc chặn — và bất biến quan trọng nhất: MẶC ĐỊNH LÀ NO-OP.

Mọi rollout của repo này ship ở trạng thái tắt rồi mới bật bằng env (GROUNDING_ENABLED,
TIERING_ENABLED, CV_SCREENING_ENABLED, TRANSCRIBE_SEND_ORIGINAL...). Nếu ai đó đổi mặc định
`thread_pool_max_workers` sang một số > 0 thì KHÔNG có gì hỏng ngay — chỉ là lần deploy kế
đổi hành vi đồng thời của cả hai tiến trình mà không ai chủ ý, trên đúng box 7,6 GB nơi mỗi
request đang bay giữ ~15 MB.
"""
from concurrent.futures import ThreadPoolExecutor

import pytest

from app import threadpool
from app.config import Settings


def test_mac_dinh_la_KHONG_doi_gi():
    """0 = giữ nguyên executor mặc định của asyncio."""
    assert Settings(gemini_api_key="x").thread_pool_max_workers == 0
    assert threadpool.resolve_executor(0) is None


@pytest.mark.parametrize("n", [0, -1, -64])
def test_gia_tri_khong_duong_KHONG_dung_executor(n):
    """Số âm/0 phải trả None chứ KHÔNG ném.

    `ThreadPoolExecutor(max_workers=0)` ném ValueError ⇒ nếu để lọt xuống thì một biến env gõ
    nhầm biến thành container chết lúc khởi động, mà đây là đường khởi động của CẢ api LẪN worker.
    """
    assert threadpool.resolve_executor(n) is None


@pytest.mark.parametrize("n", [1, 32, 64])
def test_gia_tri_duong_dung_executor_dung_co(n):
    ex = threadpool.resolve_executor(n)
    try:
        assert isinstance(ex, ThreadPoolExecutor)
        assert ex._max_workers == n
    finally:
        ex.shutdown(wait=False)


def test_apply_chi_dat_executor_khi_co_cau_hinh():
    """Vế phủ định: mặc định KHÔNG được gọi `set_default_executor`.

    Gọi nó với None sẽ xoá executor mặc định của loop — hỏng mọi `asyncio.to_thread` sau đó.
    """
    calls = []

    class _Loop:
        def set_default_executor(self, ex):
            calls.append(ex)

    loop = _Loop()

    assert threadpool.apply(loop, 0) is None
    assert calls == [], "mặc định phải KHÔNG chạm loop"

    ex = threadpool.apply(loop, 8)
    try:
        assert calls == [ex], "cấu hình > 0 mới được đặt, và đúng executor vừa dựng"
    finally:
        ex.shutdown(wait=False)


def test_thread_co_ten_de_doc_duoc_khi_dump_stack():
    """Đặt tên không phải trang trí: lúc điều tra treo/OOM, `py-spy dump` mà toàn `Thread-7`
    thì không phân biệt được thread chặn của ta với thread của thư viện."""
    ex = threadpool.resolve_executor(2)
    try:
        assert ex._thread_name_prefix.startswith("isas")
    finally:
        ex.shutdown(wait=False)


def test_lifespan_cua_app_that_su_chay_va_khong_nem():
    """🔴 Lỗ dễ bỏ sót: `TestClient(app)` KHÔNG chạy lifespan — chỉ `with TestClient(app)` mới chạy.

    Mọi test hiện có trong repo dùng dạng thứ nhất (`client = TestClient(main_module.app)` ở
    module scope), nên một `lifespan` ném sẽ đi qua sạch 397 test rồi mới chết ở lần khởi động
    THẬT. Test này là chỗ duy nhất đi qua đường đó.
    """
    from fastapi.testclient import TestClient

    from app import main as main_module

    with TestClient(main_module.app) as c:      # `with` = chạy startup + shutdown thật
        assert c.get("/api/v1/health").status_code == 200


def test_lifespan_ton_trong_cau_hinh(monkeypatch):
    """Và nó phải THẬT SỰ đọc cấu hình, không phải chỉ chạy qua."""
    from fastapi.testclient import TestClient

    from app import main as main_module
    from app.config import settings as app_settings

    seen = []
    monkeypatch.setattr(main_module.threadpool, "apply",
                        lambda loop, n: seen.append(n))
    monkeypatch.setattr(app_settings, "thread_pool_max_workers", 17)

    with TestClient(main_module.app):
        pass

    assert seen == [17], "lifespan phải chuyển đúng giá trị cấu hình xuống threadpool.apply"
