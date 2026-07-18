# tests/test_tts.py — TTS đọc câu hỏi thành tiếng: POST /api/v1/tts + cache S3 theo nội dung.
#
# Mock provider.synthesize_speech (KHÔNG gọi Gemini thật) + monkeypatch storage (KHÔNG đụng S3)
# + monkeypatch audio.pcm_to_mp3 (KHÔNG cần ffmpeg trên máy chạy test) — mirror test_face_verify.py
# / test_decide_next.py. conftest stub faster_whisper/insightface + GEMINI_API_KEY dummy.
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
