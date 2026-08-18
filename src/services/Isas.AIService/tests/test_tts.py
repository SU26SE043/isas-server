# tests/test_tts.py — TTS đọc câu hỏi thành tiếng: POST /api/v1/tts + cache S3 theo nội dung.
#
# Mock provider.synthesize_speech (KHÔNG gọi Gemini thật) + monkeypatch storage (KHÔNG đụng S3)
# + monkeypatch audio.pcm_to_mp3 (KHÔNG cần ffmpeg trên máy chạy test) — mirror test_face_verify.py
# / test_decide_next.py. conftest stub faster_whisper/insightface + GEMINI_API_KEY dummy.
import asyncio
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app import audio, tts
from app.config import settings

client = TestClient(main_module.app)

_HEADERS = {"X-Internal-Token": settings.internal_token}

_QUESTION = "Bạn hiểu Dependency Injection thế nào?"
_PCM = b"\x00\x01" * 100                     # PCM giả (nội dung không quan trọng)
_MP3 = b"ID3\x03\x00fake-mp3-bytes"          # mp3 giả


@pytest.fixture
def fake_s3(monkeypatch):
    """S3 giả trong RAM: dict key→bytes. Trả về dict để test soi/nạp sẵn cache."""
    store: dict[str, bytes] = {}

    monkeypatch.setattr(main_module.storage, "try_get_object_bytes",
                        lambda key: store.get(key))
    monkeypatch.setattr(main_module.storage, "put_object_bytes",
                        lambda key, data, content_type: store.__setitem__(key, data))
    return store


@pytest.fixture
def fake_vendor(monkeypatch):
    """provider.synthesize_speech giả → trả PCM cố định. Đếm số lần gọi qua .call_count."""
    mock = AsyncMock(return_value=(_PCM, "audio/L16;codec=pcm;rate=24000"))
    monkeypatch.setattr(main_module.provider, "synthesize_speech", mock)
    # ffmpeg không chắc có trên máy dev/CI → thay bằng hàm giả (encode thật test ở
    # test_pcm_to_mp3_* dưới, và verify tay trong image có ffmpeg).
    monkeypatch.setattr(main_module.audio, "pcm_to_mp3", lambda pcm, rate=24000: _MP3)
    return mock


# ── TIẾT KIỆM TIỀN: cache hit KHÔNG được gọi vendor ────────────────────────────────
def test_cache_hit_khong_goi_vendor(fake_s3, fake_vendor):
    """Câu hỏi đã có audio trong S3 → trả thẳng, vendor KHÔNG được gọi lần nào.

    Đây là bất biến quan trọng nhất của thiết kế cache: câu hỏi trùng (nhất là seed B2B
    phát cho mọi ứng viên) chỉ được tính tiền ĐÚNG MỘT LẦN."""
    key = tts.cache_key(_QUESTION, settings.tts_voice)
    fake_s3[key] = _MP3                       # nạp sẵn cache

    resp = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert resp.status_code == 200
    assert resp.content == _MP3
    assert resp.headers["content-type"] == "audio/mpeg"
    assert resp.headers["X-Tts-Cache"] == "hit"
    fake_vendor.assert_not_awaited()          # ⇐ Times.Never
    assert fake_vendor.call_count == 0


def test_hai_request_cung_cau_hoi_chi_goi_vendor_mot_lan(fake_s3, fake_vendor):
    """Miss rồi hit: request 1 tổng hợp + ghi cache, request 2 ăn cache → vendor 1 lần."""
    r1 = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)
    r2 = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert r1.status_code == r2.status_code == 200
    assert r1.headers["X-Tts-Cache"] == "miss"
    assert r2.headers["X-Tts-Cache"] == "hit"
    assert r1.content == r2.content == _MP3
    assert fake_vendor.call_count == 1


def test_cache_miss_tong_hop_va_ghi_s3(fake_s3, fake_vendor):
    """Miss → gọi vendor đúng 1 lần, ghi đúng key nội-dung-định-danh, trả mp3."""
    resp = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert resp.status_code == 200
    assert resp.headers["X-Tts-Cache"] == "miss"
    key = tts.cache_key(_QUESTION, settings.tts_voice)
    assert fake_s3[key] == _MP3               # đã nằm trong cache cho lần sau
    fake_vendor.assert_awaited_once()
    # Đọc bằng ngôn ngữ cấu hình phía server (vi-VN), không để client truyền.
    assert fake_vendor.await_args.args[2] == settings.tts_language_code


@pytest.mark.asyncio
async def test_cache_miss_dong_thoi_dung_chung_mot_luot_vendor(fake_s3, monkeypatch):
    """Hai request cùng key khi cache còn miss phải join cùng task, không nhân đôi quota TTS."""
    started = asyncio.Event()
    release = asyncio.Event()

    async def slow_vendor(*_args):
        started.set()
        await release.wait()
        return _PCM, "audio/L16;codec=pcm;rate=24000"

    vendor = AsyncMock(side_effect=slow_vendor)
    monkeypatch.setattr(main_module.provider, "synthesize_speech", vendor)
    monkeypatch.setattr(main_module.audio, "pcm_to_mp3", lambda pcm, rate=24000: _MP3)
    main_module._tts_inflight.clear()
    key = tts.cache_key(_QUESTION, settings.tts_voice)

    first = asyncio.create_task(main_module._get_or_create_tts(
        key, _QUESTION, settings.tts_voice, settings.tts_language_code))
    await started.wait()
    second = asyncio.create_task(main_module._get_or_create_tts(
        key, _QUESTION, settings.tts_voice, settings.tts_language_code))
    await asyncio.sleep(0)

    assert vendor.call_count == 1
    release.set()
    assert await first == await second == (_MP3, "miss")
    assert fake_s3[key] == _MP3


@pytest.mark.asyncio
async def test_browser_huy_request_van_de_luot_tts_ghi_cache(fake_s3, monkeypatch):
    """FE fail-open sau 9s không được giết lượt vendor đang làm cache cho lần nghe lại."""
    started = asyncio.Event()
    release = asyncio.Event()

    async def slow_vendor(*_args):
        started.set()
        await release.wait()
        return _PCM, "audio/L16;codec=pcm;rate=24000"

    monkeypatch.setattr(main_module.provider, "synthesize_speech", slow_vendor)
    monkeypatch.setattr(main_module.audio, "pcm_to_mp3", lambda pcm, rate=24000: _MP3)
    main_module._tts_inflight.clear()
    key = tts.cache_key(_QUESTION, settings.tts_voice)

    request = asyncio.create_task(main_module._get_or_create_tts(
        key, _QUESTION, settings.tts_voice, settings.tts_language_code))
    await started.wait()
    vendor_task = main_module._tts_inflight[key]
    request.cancel()
    with pytest.raises(asyncio.CancelledError):
        await request

    assert not vendor_task.cancelled()
    release.set()
    await vendor_task
    assert fake_s3[key] == _MP3


@pytest.mark.asyncio
async def test_vendor_treo_bi_cat_boi_tran_60s(fake_s3, monkeypatch):
    """Mọi đường mở cache/UI phụ thuộc vendor phải có trần, không giữ lock vô hạn."""
    never_finishes = asyncio.Event()

    async def hanging_vendor(*_args):
        await never_finishes.wait()

    monkeypatch.setattr(main_module.provider, "synthesize_speech", hanging_vendor)
    monkeypatch.setattr(settings, "tts_synthesis_timeout_seconds", 0.01)
    main_module._tts_inflight.clear()
    key = tts.cache_key(_QUESTION, settings.tts_voice)

    with pytest.raises(TimeoutError):
        await main_module._get_or_create_tts(
            key, _QUESTION, settings.tts_voice, settings.tts_language_code)
    assert key not in main_module._tts_inflight


@pytest.mark.asyncio
async def test_warmup_cau_vua_sinh_ghi_cache_truoc_khi_fe_goi(fake_s3, fake_vendor):
    """Câu seed/adaptive vừa sinh được làm nóng ở nền và dedup cùng key."""
    main_module._tts_inflight.clear()

    await main_module._warm_tts_batch([_QUESTION, _QUESTION], "vi")

    key = tts.cache_key(_QUESTION, settings.tts_voice)
    assert fake_s3[key] == _MP3
    assert fake_vendor.call_count == 1


@pytest.mark.asyncio
async def test_adaptive_warmup_cho_audio_vao_cache_truoc_khi_tra_question(
    fake_s3, fake_vendor, monkeypatch,
):
    monkeypatch.setattr(settings, "tts_prewarm_enabled", True)
    monkeypatch.setattr(settings, "tts_adaptive_prewarm_wait_seconds", 1.0)

    await main_module._prewarm_adaptive_tts(_QUESTION, "vi")

    key = tts.cache_key(_QUESTION, settings.tts_voice)
    assert fake_s3[key] == _MP3
    assert fake_vendor.call_count == 1


@pytest.mark.asyncio
async def test_adaptive_warmup_het_tran_van_giu_task_chay_nen(
    fake_s3, fake_vendor, monkeypatch,
):
    async def slow_vendor(*_args):
        await asyncio.sleep(0.04)
        return _PCM, "audio/L16;codec=pcm;rate=24000"

    fake_vendor.side_effect = slow_vendor
    monkeypatch.setattr(settings, "tts_prewarm_enabled", True)
    monkeypatch.setattr(settings, "tts_adaptive_prewarm_wait_seconds", 0.005)

    await main_module._prewarm_adaptive_tts(_QUESTION, "vi")
    key = tts.cache_key(_QUESTION, settings.tts_voice)
    assert key not in fake_s3

    await asyncio.sleep(0.06)
    assert fake_s3[key] == _MP3


@pytest.mark.asyncio
async def test_warmup_hai_lane_de_cau_sau_khong_cho_cau_dau(fake_s3, monkeypatch):
    """Concurrency=2 giảm thời gian tới câu 4/5 nhưng không burst toàn bộ batch."""
    active = 0
    max_active = 0
    two_started = asyncio.Event()
    release = asyncio.Event()

    async def vendor(*_args):
        nonlocal active, max_active
        active += 1
        max_active = max(max_active, active)
        if active == 2:
            two_started.set()
        await release.wait()
        active -= 1
        return _PCM, "audio/L16;codec=pcm;rate=24000"

    monkeypatch.setattr(main_module.provider, "synthesize_speech", AsyncMock(side_effect=vendor))
    monkeypatch.setattr(main_module.audio, "pcm_to_mp3", lambda pcm, rate=24000: _MP3)
    monkeypatch.setattr(settings, "tts_prewarm_concurrency", 2)
    main_module._tts_inflight.clear()

    warmup = asyncio.create_task(main_module._warm_tts_batch(
        [f"{_QUESTION} {index}" for index in range(3)], "vi"))
    await asyncio.wait_for(two_started.wait(), timeout=0.2)

    assert max_active == 2
    release.set()
    await warmup
    assert len(fake_s3) == 3


def test_doi_noi_dung_cau_hoi_thi_khong_dung_lai_audio_cu(fake_s3, fake_vendor):
    """Sửa câu hỏi ⇒ hash đổi ⇒ cache cũ KHÔNG bị đọc nhầm (tự vô hiệu hoá)."""
    client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)
    client.post("/api/v1/tts", json={"text": _QUESTION + " Cho ví dụ."}, headers=_HEADERS)

    assert fake_vendor.call_count == 2
    assert len(fake_s3) == 2                  # 2 file riêng biệt


def test_doi_giong_doc_thi_ra_file_khac(fake_s3, fake_vendor):
    """voice nằm trong hash → cùng text nhưng khác giọng = khác key."""
    client.post("/api/v1/tts", json={"text": _QUESTION, "voice": "Kore"}, headers=_HEADERS)
    client.post("/api/v1/tts", json={"text": _QUESTION, "voice": "Puck"}, headers=_HEADERS)

    assert fake_vendor.call_count == 2
    assert len(fake_s3) == 2


# ── Lỗi vendor → 502 sạch, không chặn luồng phỏng vấn ──────────────────────────────
def test_vendor_loi_tra_502(fake_s3, monkeypatch):
    """Gemini chết/quá tải → 502 (FE degrade về chỉ hiện chữ), KHÔNG ghi cache rác."""
    monkeypatch.setattr(main_module.provider, "synthesize_speech",
                        AsyncMock(side_effect=RuntimeError("Gemini 503 high demand")))

    resp = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert resp.status_code == 502
    assert fake_s3 == {}                      # không lưu gì khi tổng hợp hỏng


def test_encode_mp3_loi_tra_502(fake_s3, monkeypatch):
    """ffmpeg lỗi → 502, không trả PCM thô cho client (FE không phát được)."""
    monkeypatch.setattr(main_module.provider, "synthesize_speech",
                        AsyncMock(return_value=(_PCM, "audio/L16;codec=pcm;rate=24000")))
    monkeypatch.setattr(main_module.audio, "pcm_to_mp3",
                        lambda pcm, rate=24000: (_ for _ in ()).throw(RuntimeError("no ffmpeg")))

    resp = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert resp.status_code == 502
    assert fake_s3 == {}


def test_ghi_cache_hong_van_tra_audio(fake_vendor, monkeypatch):
    """S3 ghi hỏng KHÔNG làm hỏng request — vẫn trả audio, nhưng đánh dấu miss-nostore."""
    monkeypatch.setattr(main_module.storage, "try_get_object_bytes", lambda key: None)
    monkeypatch.setattr(main_module.storage, "put_object_bytes",
                        lambda key, data, content_type: (_ for _ in ()).throw(OSError("s3 down")))

    resp = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert resp.status_code == 200
    assert resp.content == _MP3
    assert resp.headers["X-Tts-Cache"] == "miss-nostore"


def test_doc_cache_loi_that_tra_502_khong_goi_vendor(fake_vendor, monkeypatch):
    """S3 hỏng THẬT (≠ "chưa có") → 502, KHÔNG lặng lẽ fallback sang gọi vendor."""
    monkeypatch.setattr(main_module.storage, "try_get_object_bytes",
                        lambda key: (_ for _ in ()).throw(OSError("s3 unreachable")))

    resp = client.post("/api/v1/tts", json={"text": _QUESTION}, headers=_HEADERS)

    assert resp.status_code == 502
    assert fake_vendor.call_count == 0


# ── Gate + validate ───────────────────────────────────────────────────────────────
def test_thieu_internal_token_tra_401(fake_s3, fake_vendor):
    """GEN-7 fail-closed: endpoint máy-máy, không token → 401, không gọi vendor."""
    resp = client.post("/api/v1/tts", json={"text": _QUESTION})

    assert resp.status_code == 401
    assert fake_vendor.call_count == 0


def test_sai_internal_token_tra_401(fake_s3, fake_vendor):
    resp = client.post("/api/v1/tts", json={"text": _QUESTION},
                       headers={"X-Internal-Token": "sai-token"})

    assert resp.status_code == 401
    assert fake_vendor.call_count == 0


def test_text_rong_tra_400(fake_s3, fake_vendor):
    """Rỗng/toàn khoảng trắng → 400 TRƯỚC khi gọi vendor (không đốt tiền vô ích)."""
    resp = client.post("/api/v1/tts", json={"text": "   "}, headers=_HEADERS)

    assert resp.status_code == 400
    assert fake_vendor.call_count == 0


# ── Cache key: ổn định + không đụng nhau ──────────────────────────────────────────
def test_cache_key_on_dinh_va_dung_dinh_dang():
    """Cùng (text, voice) ⇒ cùng key; đúng tiền tố + đuôi .mp3 (GEN-5: lưu KEY, không URL)."""
    k1 = tts.cache_key(_QUESTION, "Kore")
    k2 = tts.cache_key(_QUESTION, "Kore")

    assert k1 == k2
    assert k1.startswith(settings.tts_cache_prefix)
    assert k1.endswith(".mp3")
    assert "://" not in k1                    # là key, không phải full URL
    assert tts.cache_key(_QUESTION, "Puck") != k1


def test_cache_key_khong_nhap_nhang_khi_ghep_chuoi():
    """(voice='A', text='BC') ≠ (voice='AB', text='C') — separator chống đụng key."""
    assert tts.cache_key("BC", "A") != tts.cache_key("C", "AB")


# ── Parse sample-rate từ mime_type Gemini ─────────────────────────────────────────
@pytest.mark.parametrize("mime,expected", [
    ("audio/L16;codec=pcm;rate=24000", 24000),
    ("audio/L16;codec=pcm;rate=16000", 16000),
    ("audio/L16;codec=pcm", audio.DEFAULT_SAMPLE_RATE),   # thiếu rate → mặc định
    (None, audio.DEFAULT_SAMPLE_RATE),
    ("audio/L16;rate=khong-phai-so", audio.DEFAULT_SAMPLE_RATE),
])
def test_parse_pcm_rate(mime, expected):
    assert audio.parse_pcm_rate(mime) == expected


def test_pcm_to_mp3_pcm_rong_thi_nem():
    """PCM rỗng = vendor trả rác → ném để caller map 502, không ghi file 0 byte vào cache."""
    with pytest.raises(RuntimeError):
        audio.pcm_to_mp3(b"")
