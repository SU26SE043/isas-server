# tests/test_gemini_transient_retry.py — thử lại lỗi TẠM THỜI của Gemini tại chokepoint (2026-08-20).
#
# Chuyện phải chữa: log worker prod có nguyên văn `[⚠️] Lỗi tạm thời answer …: 503 UNAVAILABLE ->
# nack (republish sau)`. Một cú 503 chưa tới một giây khi đó thành **15 phút** người dùng ngồi
# chờ, vì message phải đợi `StuckAnswerRepublisher` (.NET, quét mỗi 2') đẩy lại. Lưới cứu hộ đó
# dành cho sự cố KÉO DÀI; dùng nó để đỡ một cú nấc mạng là sai tầng.
#
# Vá tại `GeminiProvider._generate` — chokepoint DUY NHẤT của mọi lượt gọi Gemini (F22) — nên một
# vòng retry phủ luôn `score`, `decide_next`, `summarize_session` và mọi endpoint thêm sau này.
#
# Bộ test này khoá 3 điều dễ trôi mất, cả 3 đều hỏng ÂM THẦM:
#   (1) lỗi tạm thời PHẢI được thử lại — mất nó là quay về 15 phút chờ, không lỗi nào nổ;
#   (2) lỗi vĩnh viễn (4xx) và `ValueError` PHẢI raise NGAY — bắt nhầm `ValueError` ở tầng này thì
#       nó NHÂN với `score_max_attempts` thành 9 lượt gọi Gemini cho một answer;
#   (3) ngân sách thời gian ≤ ~5s — `decide_next` chạy đồng bộ dưới timeout 90s của .NET.
# Không gọi Gemini thật (mock `generate_content`, mẫu test_scoring.py).
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest
from google.genai import errors as genai_errors

from app.config import Settings, settings
from app.providers import gemini as gemini_module
from app.providers.gemini import GeminiProvider, _is_transient_api_error


def _api_error(code: int, status: str) -> genai_errors.APIError:
    """Dựng lỗi ĐÚNG hình dạng SDK ném ra (`code` từ tầng HTTP, `status` từ body)."""
    cls = genai_errors.ServerError if code >= 500 else genai_errors.ClientError
    return cls(code, {"error": {"code": code, "status": status, "message": "dựng cho test"}})


_OK_RESPONSE = SimpleNamespace(text='{"ok":true}', usage_metadata=None)


@pytest.fixture
def provider(monkeypatch):
    """Provider với client mock + `report_usage` mock + `sleep` không thật sự ngủ.

    `sleep` giả GHI LẠI độ trễ rồi nhường loop ngay: bài test phải khẳng định được backoff nhân
    đôi (1s → 2s) mà không kéo bộ test dài thêm 3 giây mỗi ca.
    """
    p = GeminiProvider()
    p._client = SimpleNamespace(aio=SimpleNamespace(models=SimpleNamespace(
        generate_content=AsyncMock())))

    delays: list[float] = []
    real_sleep = asyncio.sleep

    async def fake_sleep(seconds):
        delays.append(seconds)
        await real_sleep(0)

    monkeypatch.setattr(gemini_module.asyncio, "sleep", fake_sleep)
    monkeypatch.setattr(gemini_module, "report_usage", AsyncMock())
    p._test_delays = delays
    return p


# ── Phân loại lỗi: danh sách TRẮNG, mặc định là "không thử lại" ────────────────
@pytest.mark.parametrize("error", [
    _api_error(503, "UNAVAILABLE"),        # mã bắt được trong log prod
    _api_error(429, "RESOURCE_EXHAUSTED"),  # mã còn lại đáng thử lại của Gemini
    _api_error(500, "INTERNAL"),
    _api_error(504, "DEADLINE_EXCEEDED"),
    httpx.ConnectError("đứt mạng"),         # chưa tới được Gemini ⇒ không có mã HTTP để đọc
    httpx.ReadTimeout("vendor không trả lời kịp"),
    TimeoutError(),
])
def test_nhan_dien_loi_tam_thoi(error):
    assert _is_transient_api_error(error) is True


@pytest.mark.parametrize("error", [
    _api_error(400, "INVALID_ARGUMENT"),   # request/schema sai — gọi lại y hệt nhận lại y hệt
    _api_error(401, "UNAUTHENTICATED"),
    _api_error(403, "PERMISSION_DENIED"),
    _api_error(404, "NOT_FOUND"),
    ValueError("LLM output không hợp lệ"),  # xem test_khong_bao_gio_thu_lai_valueerror
    TypeError("lập trình sai"),
])
def test_nhan_dien_loi_vinh_vien(error):
    assert _is_transient_api_error(error) is False


# ── Hành vi của vòng retry ────────────────────────────────────────────────────
@pytest.mark.asyncio
async def test_503_mot_lan_roi_thanh_cong(provider):
    """503 chớp nhoáng → thử lại → trả kết quả BÌNH THƯỜNG cho caller.

    Đây là toàn bộ lý do tồn tại của vòng này: worker KHÔNG được thấy lỗi, tức không nack, tức
    người dùng không phải chờ republisher.
    """
    provider._client.aio.models.generate_content.side_effect = [
        _api_error(503, "UNAVAILABLE"), _OK_RESPONSE]

    response = await provider._generate("score", contents="p", config=None)

    assert response is _OK_RESPONSE
    assert provider._client.aio.models.generate_content.await_count == 2
    assert provider._test_delays == [1.0]   # chờ đúng 1 nhịp đầu


@pytest.mark.asyncio
async def test_can_luot_thi_raise_va_backoff_nhan_doi(provider):
    """Lỗi tạm thời MỌI lượt → cạn `gemini_retry_attempts` → ném nguyên si lỗi cuối.

    Ném nguyên si (không nuốt, không dịch nghĩa) vì worker mới là chỗ biết phải nack hay báo
    Failed — chokepoint không có ngữ cảnh đó.
    """
    provider._client.aio.models.generate_content.side_effect = _api_error(503, "UNAVAILABLE")

    with pytest.raises(genai_errors.APIError) as excinfo:
        await provider._generate("decide_next", contents="p", config=None)

    assert excinfo.value.code == 503
    assert (provider._client.aio.models.generate_content.await_count
            == settings.gemini_retry_attempts)
    assert provider._test_delays == [1.0, 2.0]   # backoff nhân đôi, KHÔNG phải 1s phẳng


@pytest.mark.asyncio
async def test_loi_vinh_vien_raise_ngay_khong_cho(provider):
    """400 = request/schema sai: gọi lại y hệt nhận lại y hệt ⇒ raise NGAY, không ngủ nhịp nào."""
    provider._client.aio.models.generate_content.side_effect = _api_error(400, "INVALID_ARGUMENT")

    with pytest.raises(genai_errors.APIError):
        await provider._generate("score", contents="p", config=None)

    assert provider._client.aio.models.generate_content.await_count == 1
    assert provider._test_delays == []


@pytest.mark.asyncio
async def test_khong_bao_gio_thu_lai_valueerror(provider):
    """🔴 Chốt chặn quan trọng nhất file này.

    Output hỏng đã có vòng retry RIÊNG ở caller (`score_max_attempts=3`,
    `decide_next_max_attempts=2`). Nới `_generate` ra bắt luôn `ValueError` thì hai vòng NHÂN với
    nhau — 3×3 = 9 lượt gọi Gemini cho một answer — mà không test nào đỏ và không log nào nói vì
    sao hoá đơn token gấp ba.
    """
    provider._client.aio.models.generate_content.side_effect = ValueError("JSON cụt")

    with pytest.raises(ValueError):
        await provider._generate("score", contents="p", config=None)

    assert provider._client.aio.models.generate_content.await_count == 1
    assert provider._test_delays == []


@pytest.mark.asyncio
async def test_loi_mang_duoc_thu_lai(provider):
    """Đứt mạng/timeout: lượt gọi chưa tới được Gemini nên không có mã HTTP — phải nhận diện
    bằng kiểu exception, nếu không đúng nhóm lỗi TẠM THỜI NHẤT lại là nhóm không được thử lại."""
    provider._client.aio.models.generate_content.side_effect = [
        httpx.ConnectError("đứt mạng"), _OK_RESPONSE]

    assert await provider._generate("summarize_session", contents="p", config=None) is _OK_RESPONSE
    assert provider._client.aio.models.generate_content.await_count == 2


# ── F22: đo token KHÔNG được lệch vì vòng retry ───────────────────────────────
@pytest.mark.asyncio
async def test_report_usage_dung_mot_lan_cho_luot_thanh_cong(provider):
    """Ghi nhận ĐÚNG MỘT LẦN, cho lượt THÀNH CÔNG.

    Lượt hỏng ném exception nên không có response — cũng không có `usage_metadata` để đọc. Ghi
    thêm cho nó là bịa ra một dòng thống kê 0 token; ghi thiếu cho lượt thành công là mất đúng
    phần chi phí F22 sinh ra để thấy.
    """
    provider._client.aio.models.generate_content.side_effect = [
        _api_error(503, "UNAVAILABLE"), _api_error(503, "UNAVAILABLE"), _OK_RESPONSE]

    await provider._generate("score", contents="p", config=None)

    gemini_module.report_usage.assert_awaited_once()
    operation, model, response = gemini_module.report_usage.await_args.args
    assert operation == "score"
    assert response is _OK_RESPONSE


@pytest.mark.asyncio
async def test_report_usage_khong_ghi_cho_luot_hong(provider):
    """Cạn lượt thử → không có response nào → KHÔNG dòng thống kê nào."""
    provider._client.aio.models.generate_content.side_effect = _api_error(503, "UNAVAILABLE")

    with pytest.raises(genai_errors.APIError):
        await provider._generate("score", contents="p", config=None)

    gemini_module.report_usage.assert_not_awaited()


@pytest.mark.asyncio
async def test_defer_report_van_giu_nguyen_giao_uoc(provider):
    """`defer_report=True` (lesson-theory tự gọi report_usage) không được vòng retry làm lệch."""
    provider._client.aio.models.generate_content.side_effect = [
        _api_error(503, "UNAVAILABLE"), _OK_RESPONSE]

    await provider._generate("generate_lesson_theory", contents="p", config=None,
                             defer_report=True)

    gemini_module.report_usage.assert_not_awaited()


# ── Ngân sách thời gian: RÀNG BUỘC CỨNG, không phải sở thích ──────────────────
def test_tong_thoi_gian_retry_nam_trong_ngan_sach_cua_decide_next():
    """`_generate` nằm trên CẢ `/decide-next` — đường chạy ĐỒNG BỘ trong request upload câu trả
    lời của người dùng, dưới timeout **90s** phía .NET (cả request đo ~9,4s).

    Nới attempts/backoff ăn thẳng vào ngân sách đó và biến một cú 503 của Gemini thành timeout
    của .NET — hỏng to hơn cái đang vá. Test này là cái chuông: muốn kiên nhẫn hơn cho đường
    ASYNC (worker chấm) thì phải TÁCH cấu hình theo đường gọi, đừng nâng số dùng chung.
    """
    total = sum(settings.gemini_retry_backoff_seconds * (2 ** i)
                for i in range(settings.gemini_retry_attempts - 1))
    assert total <= 5.0, (
        f"tổng {total}s nằm chờ vượt ngân sách ~5s của /decide-next "
        "(đồng bộ, dưới timeout 90s của .NET)")


def test_mac_dinh_va_kill_switch():
    """3 = 1 lượt đầu + 2 lượt thử lại; `1` = tắt hẳn (về hành vi trước bản vá)."""
    assert settings.gemini_retry_attempts == 3
    assert settings.gemini_retry_backoff_seconds == 1.0
    assert Settings(gemini_retry_attempts=1).gemini_retry_attempts == 1


@pytest.mark.asyncio
async def test_attempts_1_tat_han_viec_thu_lai(provider, monkeypatch):
    """Kill-switch phải THẬT SỰ tắt: 1 lượt, không ngủ, ném ngay."""
    monkeypatch.setattr(settings, "gemini_retry_attempts", 1)
    provider._client.aio.models.generate_content.side_effect = _api_error(503, "UNAVAILABLE")

    with pytest.raises(genai_errors.APIError):
        await provider._generate("score", contents="p", config=None)

    assert provider._client.aio.models.generate_content.await_count == 1
    assert provider._test_delays == []
