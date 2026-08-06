# tests/test_transcribe_providers.py — chép lời qua nhà cung cấp TỪ XA, Whisper cục bộ dự phòng.
#
# VÌ SAO CÓ FILE NÀY: Whisper `small` (bản đang chạy prod) chép sai tới mức ĐỔI NGHĨA — "người
# dùng cần thiết" → "người dùng tầng thiết", "Business Analyst" → "BGN Analyze". Bản chép đó đi
# THẲNG vào bộ chấm. Đo trên 7 ghi âm thật: lỗi từ 4,2% (small) so với 0,7% (whisper-1) / 0,5%
# (gemini), mà hai nhà cung cấp từ xa còn NHANH hơn.
#
# Ba bất biến file này khoá lại, xếp theo mức dễ vỡ:
#   1. MỐC THỜI GIAN vẫn của VAD kể cả khi phần CHỮ đến từ nơi khác (F11 không được rơi rụng).
#   2. Từ xa hỏng → RƠI VỀ cục bộ, và CON DẤU phải nói đúng nơi đã chép (không thì "điểm thấp do
#      ứng viên hay do bản chép?" thành câu không trả lời được).
#   3. Payload gửi OpenAI KHÔNG BAO GIỜ chứa khoá `prompt` — xem test cuối file.
import json

import pytest

from app import transcriber as transcriber_mod
from app import transcribe_providers as tp
from app.config import settings as app_settings
from app.transcriber import SAMPLE_RATE, Transcriber


class _Seg:
    def __init__(self, start, end, text):
        self.start, self.end, self.text = start, end, text


def _pcm(seconds: float = 6.0) -> list[float]:
    return [0.0] * int(seconds * SAMPLE_RATE)


# Whisper cục bộ và VAD cố ý kể HAI câu chuyện khác nhau: nếu cho chúng trả giống nhau thì test
# vẫn xanh kể cả khi ai đó lấy mốc thời gian từ nhà cung cấp từ xa (mẫu test_delivery_metrics_vad).
_LOCAL_SEGS = [_Seg(0.0, 5.0, "bản cục bộ"), _Seg(5.5, 6.0, "nói tiếp")]
_VAD_SPANS = [
    {"start": 0, "end": 1 * SAMPLE_RATE},
    {"start": 4 * SAMPLE_RATE, "end": 6 * SAMPLE_RATE},
]


@pytest.fixture(autouse=True)
def _reset_provider(monkeypatch):
    """Mặc định `local` — mọi test tự khai nhà cung cấp nó muốn."""
    monkeypatch.setattr(app_settings, "transcribe_provider", "local")
    monkeypatch.setattr(app_settings, "whisper_model", "small")
    monkeypatch.setattr(app_settings, "delivery_metrics_source", "vad")
    monkeypatch.setattr(app_settings, "transcribe_send_original", False)


def _make(monkeypatch, *, vad_spans=None, seconds=6.0):
    """Transcriber đã chặn mọi cửa I/O; trả (transcriber, sổ ghi lời gọi)."""
    calls: dict = {"local_transcribe": 0, "vad_audio": None}
    pcm = _pcm(seconds)

    monkeypatch.setattr(transcriber_mod, "decode_audio", lambda *a, **k: pcm)

    def _vad(audio, vad_options=None, **kwargs):
        calls["vad_audio"] = audio
        return _VAD_SPANS if vad_spans is None else vad_spans

    monkeypatch.setattr(transcriber_mod, "get_speech_timestamps", _vad)

    class _Model:
        def transcribe(self, audio, **kwargs):
            calls["local_transcribe"] += 1
            return _LOCAL_SEGS, None

    t = Transcriber.__new__(Transcriber)      # bỏ qua __init__ (nạp model thật)
    t._model = _Model()
    return t, calls


def _stub_remote(monkeypatch, text=None, *, engine="whisper-1", boom=None):
    """Thay lớp gọi mạng bằng hàm giả; ghi lại đúng những gì đã được truyền vào."""
    seen: dict = {}

    def _fake(provider, wav_bytes, language, audio_seconds=0.0):
        seen.update(provider=provider, wav=wav_bytes, language=language,
                    audio_seconds=audio_seconds)
        if boom is not None:
            raise boom
        return text, engine

    monkeypatch.setattr(transcriber_mod, "transcribe_remote", _fake)
    return seen


def test_original_audio_is_sent_with_real_filename_and_remote_can_retry_wav(monkeypatch, tmp_path):
    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    monkeypatch.setattr(app_settings, "transcribe_send_original", True)
    audio = tmp_path / "answer.webm"
    audio.write_bytes(b"original-webm")
    t, _ = _make(monkeypatch)
    calls = []

    def remote(provider, data, language, audio_seconds=0.0, filename="audio.wav"):
        calls.append((data, filename))
        if filename == "answer.webm":
            raise ValueError("legacy WAV bytes under webm extension")
        return "bản WAV cứu hộ", "whisper-1"

    monkeypatch.setattr(transcriber_mod, "transcribe_remote", remote)
    assert t.transcribe_detailed(str(audio)).text == "bản WAV cứu hộ"
    assert calls[0] == (b"original-webm", "answer.webm")
    assert calls[1][1] == "audio.wav"


def test_timeout_khong_retry_wav_va_roi_thang_ve_cuc_bo(monkeypatch, tmp_path):
    """Retry timeout 60s thêm một lần sẽ vượt ngân sách decider 90s."""
    import httpx

    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    monkeypatch.setattr(app_settings, "transcribe_send_original", True)
    audio = tmp_path / "answer.webm"
    audio.write_bytes(b"original-webm")
    t, calls = _make(monkeypatch)
    attempts = []

    def remote(*args, **kwargs):
        attempts.append(args)
        raise httpx.TimeoutException("quá 60s")

    monkeypatch.setattr(transcriber_mod, "transcribe_remote", remote)
    assert t.transcribe_detailed(str(audio)).engine == "local:small"
    assert len(attempts) == 1
    assert calls["local_transcribe"] == 1


def test_loi_4xx_retry_wav_nhung_timeout_khong(monkeypatch, tmp_path):
    import httpx

    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    monkeypatch.setattr(app_settings, "transcribe_send_original", True)
    audio = tmp_path / "answer.webm"
    audio.write_bytes(b"original-webm")
    t, _ = _make(monkeypatch)
    calls = []
    request = httpx.Request("POST", "https://example.test/transcribe")
    response = httpx.Response(415, request=request)

    def remote(provider, data, language, audio_seconds=0.0, filename="audio.wav"):
        calls.append(filename)
        if len(calls) == 1:
            raise httpx.HTTPStatusError("unsupported media", request=request, response=response)
        return "bản WAV cứu hộ", "whisper-1"

    monkeypatch.setattr(transcriber_mod, "transcribe_remote", remote)
    assert t.transcribe_detailed(str(audio)).text == "bản WAV cứu hộ"
    assert calls == ["answer.webm", "audio.wav"]


def test_local_khong_doc_file_goc(monkeypatch):
    t, _ = _make(monkeypatch)
    monkeypatch.setattr(t, "_read_original", lambda _: (_ for _ in ()).throw(AssertionError("local không được đọc file gốc")))
    assert t.transcribe_detailed("/tmp/x.webm").engine == "local:small"


# ── 1. Đường thành công của từng nhà cung cấp ────────────────────────────────────────
@pytest.mark.parametrize("provider,engine", [
    ("whisper-1", "whisper-1"),
    ("gemini", "gemini-2.5-flash"),
])
def test_nha_cung_cap_tu_xa_cho_chu_va_dong_dau_dung(monkeypatch, provider, engine):
    monkeypatch.setattr(app_settings, "transcribe_provider", provider)
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch, "bản chép từ xa", engine=engine)

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "bản chép từ xa"
    assert result.engine == engine
    assert calls["local_transcribe"] == 0, "từ xa chạy được thì KHÔNG được chạy Whisper nữa"
    # Vế phủ định: không có nó thì test vẫn xanh nếu ai đó trả bản cục bộ kèm con dấu từ xa.
    assert "cục bộ" not in result.text


def test_moc_thoi_gian_VAN_tu_VAD_khi_chu_den_tu_noi_khac(monkeypatch):
    """Bất biến DỄ VỠ NHẤT của vòng này.

    Nhà cung cấp từ xa không trả biên segment. Nếu ai đó "tiện tay" lấy mốc thời gian từ nó
    (hoặc để rơi về danh sách rỗng) thì chỉ số cách nói F11 hoặc biến mất, hoặc tệ hơn: ra 0
    rồi đi vào prompt như số liệu thật — đúng hạng lỗi F11 sinh ra để diệt.
    """
    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch, "một hai ba bốn năm sáu bảy")

    m = t.transcribe_detailed("/tmp/x.webm", "vi").metrics

    assert m is not None, "có audio thật thì PHẢI đo được, dù chữ đến từ nơi khác"
    assert calls["vad_audio"] is not None, "VAD phải được chạy trên chính mảng pcm"
    # VAD: nói 0-1s, im 3s, nói 4-6s ⇒ 1 khoảng lặng 3,0s, speech 3,0/6,0.
    assert m.pause_count == 1
    assert m.longest_pause_sec == pytest.approx(3.0)
    assert m.speech_sec == pytest.approx(3.0)
    assert m.silence_ratio == pytest.approx(0.5)


def test_dem_tu_bam_theo_ban_chep_tu_xa(monkeypatch):
    """Phần CHỮ của chỉ số (đếm từ/từ đệm) phải đọc bản chép ĐANG DÙNG, không phải bản cục bộ."""
    monkeypatch.setattr(app_settings, "transcribe_provider", "gemini")
    t, _ = _make(monkeypatch)
    _stub_remote(monkeypatch, "ừm tôi từng làm dự án đó", engine="gemini-2.5-flash")

    m = t.transcribe_detailed("/tmp/x.webm", "vi").metrics

    assert m.filler_count == 1
    assert m.word_count == 7


# ── 2. Dự phòng ──────────────────────────────────────────────────────────────────────
def test_tu_xa_hong_thi_roi_ve_cuc_bo_va_KHONG_nem(monkeypatch):
    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch, boom=RuntimeError("503 từ nhà cung cấp"))

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "bản cục bộ nói tiếp", "vẫn phải có chữ, không được mất bài"
    assert result.engine == "local:small", "con dấu phải nói ĐÚNG nơi đã chép"
    assert calls["local_transcribe"] == 1


def test_timeout_cung_roi_ve_cuc_bo(monkeypatch):
    """Timeout là ca THƯỜNG GẶP nhất (mạng chậm), không phải ca hiếm — phải cùng đường với lỗi."""
    monkeypatch.setattr(app_settings, "transcribe_provider", "gemini")
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch, boom=TimeoutError("quá 60s"))

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "bản cục bộ nói tiếp"
    assert result.engine == "local:small"
    assert calls["local_transcribe"] == 1


def test_ten_nha_cung_cap_la_thi_roi_ve_cuc_bo(monkeypatch):
    """Gõ sai env (`whisper1`, `openai`…) KHÔNG được làm sập chép lời."""
    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper1")
    t, calls = _make(monkeypatch)

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "bản cục bộ nói tiếp"
    assert result.engine == "local:small"
    assert calls["local_transcribe"] == 1


def test_ban_chep_hong_thi_roi_ve_cuc_bo(monkeypatch):
    """Bản chép có dấu hiệu hỏng vẫn là chuỗi ký tự HỢP LỆ — không chặn ở đây thì bộ chấm sẽ
    chấm nó như thật."""
    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch,
                 "Hãy subscribe cho kênh Ghiền Mì Gõ Để không bỏ lỡ những video hấp dẫn. "
                 "Hãy subscribe cho kênh Ghiền Mì Gõ Để không bỏ lỡ những video hấp dẫn.")

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "bản cục bộ nói tiếp"
    assert result.engine == "local:small"
    assert calls["local_transcribe"] == 1


def test_vong_lap_cung_roi_ve_cuc_bo(monkeypatch):
    monkeypatch.setattr(app_settings, "transcribe_provider", "gemini")
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch, " ".join(["tôi từng làm dự án thanh toán ở công ty cũ"] * 4))

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.engine == "local:small"
    assert calls["local_transcribe"] == 1


def test_provider_local_thi_KHONG_goi_mang(monkeypatch):
    """Mặc định phải là đường CŨ y nguyên: không byte nào của ứng viên rời khỏi hạ tầng."""
    t, calls = _make(monkeypatch)

    def _no_network(*a, **k):
        raise AssertionError("provider='local' mà vẫn gọi ra ngoài")

    monkeypatch.setattr(transcriber_mod, "transcribe_remote", _no_network)

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "bản cục bộ nói tiếp"
    assert result.engine == "local:small"
    assert calls["local_transcribe"] == 1


# ── 3. Audio gửi đi là WAV DỰNG LẠI TỪ pcm ───────────────────────────────────────────
def test_gui_wav_dung_lai_tu_pcm_chu_khong_phai_byte_goc(monkeypatch):
    """59/77 file trong S3 mang đuôi `.webm` nhưng ruột là WAV — nhà cung cấp đoán định dạng
    theo phần mở rộng, nên gửi byte gốc là mời một lớp lỗi chỉ nổ trên một phần dữ liệu.

    Gửi WAV dựng từ chính mảng `pcm` cũng giữ bất biến "giải mã MỘT lần": nhà cung cấp và VAD
    nhìn cùng một tín hiệu.
    """
    import io
    import wave

    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    t, _ = _make(monkeypatch, seconds=2.0)
    seen = _stub_remote(monkeypatch, "ok")

    t.transcribe_detailed("/tmp/x.webm", "vi")

    wav_bytes = seen["wav"]
    assert wav_bytes[:4] == b"RIFF" and wav_bytes[8:12] == b"WAVE"
    with wave.open(io.BytesIO(wav_bytes)) as w:
        assert w.getnchannels() == 1
        assert w.getsampwidth() == 2
        assert w.getframerate() == SAMPLE_RATE
        assert w.getnframes() == int(2.0 * SAMPLE_RATE)
    # Số giây audio đi kèm để tính tiền theo PHÚT (whisper-1) — sai số này là sai hoá đơn.
    assert seen["audio_seconds"] == pytest.approx(2.0)


def test_pcm_to_wav_kep_bien_do_thay_vi_tran_so(monkeypatch):
    """Giá trị ngoài [-1,1] phải bị KẸP. Không kẹp thì int16 tràn và mẫu to nhất hoá thành mẫu
    âm to nhất — tiếng rè, mà rè thì bản chép sai chứ không lỗi gì để mà thấy."""
    wav = tp.pcm_to_wav_bytes([1.5, -1.5, 0.0])
    body = wav[44:]  # bỏ header 44 byte của WAV chuẩn
    assert int.from_bytes(body[0:2], "little", signed=True) == 32767
    assert int.from_bytes(body[2:4], "little", signed=True) == -32767


# ── 4. Bộ dò bản chép hỏng ───────────────────────────────────────────────────────────
def test_do_bat_duoc_rac_va_vong_lap():
    assert tp.looks_broken("Hãy subscribe cho kênh của mình nhé") is not None
    assert tp.looks_broken("Phụ đề được thực hiện bởi cộng đồng Amara.org") is not None
    assert tp.looks_broken(" ".join(["tôi từng làm dự án thanh toán ở công ty cũ"] * 3)) is not None
    # Lặp NGAY khối dài đúng 2 lần = chữ ký decoder kẹt (không cần tới lần thứ 3).
    assert tp.looks_broken(
        "Trong dự án gần đây tôi phụ trách phần dịch vụ thanh toán của hệ thống. "
        "Trong dự án gần đây tôi phụ trách phần dịch vụ thanh toán của hệ thống.") is not None


def test_do_bat_duoc_vong_lap_CO_TROI_giua_cac_vong():
    """Ca mà luật "khối lặp kề nhau" KHÔNG THỂ bắt — nên nếu thiếu test này thì luật thứ ba
    (cụm 6 từ xuất hiện ≥3 lần) trông y như code thừa.

    Phát hiện bằng mutation: vô hiệu luật thứ ba mà bộ test vẫn XANH, vì mọi đầu vào đang có đều
    lặp KỀ NHAU nên luật thứ hai bắt trước và luật thứ ba không bao giờ được hỏi tới. Điều tra ra
    thì đây là lỗ TEST chứ không phải code thừa: chỉ cần một từ đệm chen giữa các vòng là khối hết
    kề-đồng-nhất, mà decoder kẹt thì rất hay trôi kiểu đó.
    """
    troi = ("tôi từng làm dự án thanh toán ở công ty cũ à "
            "tôi từng làm dự án thanh toán ở công ty mới ừm "
            "tôi từng làm dự án thanh toán ở công ty đó")

    assert tp._repeated_block(tp._normalize_words(troi)) is None, \
        "tiền đề của test: luật khối-kề KHÔNG bắt được ca này"
    assert tp.looks_broken(troi) is not None, "luật ≥3 lần phải đỡ đúng ca này"


def test_do_KHONG_bat_oan_nguoi_noi_lap_that():
    """🔴 REGRESSION THẬT — văn bản dưới đây là bản chép Gemini của ghi âm r2 (ứng viên có thật).

    Ứng viên hồi hộp lặp lời: "hiểu được luồng đi của người dùng" nói hai lần, cách nhau 12 từ.
    Luật đầu tiên thử ("cụm 6 từ xuất hiện ≥2 lần") BẮT OAN đúng ca này ⇒ sẽ vứt bản chép TỐT
    NHẤT (lỗi từ 0,5%) để dùng bản cục bộ (4,2%), tức lá chắn làm chất lượng TỆ ĐI đúng trên
    nhóm câu trả lời ngập ngừng — nhóm cần chép chính xác nhất. Xem `LOOP_NGRAM_WORDS`.
    """
    that = (
        "Ờ, hiểu được luồng đi của người dùng. Bọn em bên bọn em data của bọn này chứ không gì "
        "nữa. Bọn em hiểu được rằng là hiểu được luồng đi của người dùng và người dùng cần thiết "
        "những gì là để có thể là chúng em phải hiểu được rằng là người dùng là đang cần thiết "
        "những gì. Bên bọn em và các em có thể làm việc. Thì những gì mà business analyst làm sẽ "
        "tổng hợp những thông tin cần thiết để có thể list ra một cái DB để người dùng và cho "
        "người dùng biết rằng rằng là bọn em vẫn đang nắm được luồng đi của bọn họ."
    )
    assert tp.looks_broken(that) is None, "người lặp lời THẬT không phải bản chép hỏng"


def test_do_bo_qua_ban_chep_binh_thuong():
    assert tp.looks_broken("") is None
    assert tp.looks_broken(
        "Trong dự án gần đây tôi phụ trách phần dịch vụ thanh toán của hệ thống, "
        "khó khăn lớn nhất là bảo đảm tính chính xác của tiền khi webhook đến nhiều lần."
    ) is None


# ── 5. Payload OpenAI — KHÔNG BAO GIỜ có `prompt` ────────────────────────────────────
class _FakeResp:
    def __init__(self, payload, status=200):
        self._payload = payload
        self.status_code = status

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(f"HTTP {self.status_code}")

    def json(self):
        return self._payload


class _FakeClient:
    """Bắt đúng những gì được gửi lên OpenAI."""
    last: dict = {}

    def __init__(self, *a, **k):
        type(self).last["init"] = k

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def post(self, url, headers=None, data=None, files=None, **k):
        type(self).last.update(url=url, headers=headers, data=data, files=files)
        return _FakeResp({"text": " bản chép whisper-1 "})


@pytest.fixture
def fake_httpx(monkeypatch):
    import httpx

    _FakeClient.last = {}
    monkeypatch.setattr(httpx, "Client", _FakeClient)
    monkeypatch.setattr(app_settings, "openai_api_key", "sk-test")
    monkeypatch.setattr(app_settings, "usage_sink_base", "")
    return _FakeClient


def test_payload_openai_KHONG_CHUA_prompt(fake_httpx):
    """🔴 KHOÁ CỨNG. Mồi từ vựng qua `prompt` đã được thử: trên một ghi âm thật, TOÀN BỘ câu trả
    lời của ứng viên bị thay bằng một câu kết video YouTube lặp 2 lần (vết bẩn dữ liệu huấn
    luyện Whisper). Đó là MẤT TRẮNG bài làm đã tốn 1 credit, chứ không phải chép sai vài từ.

    Nguy hiểm hơn cả: mọi chỉ số gộp lúc đó đều ĐẸP (thuật ngữ đúng 5→8, ký tự giảm 13%) — nhìn
    bảng số thì đó trông y như một cải tiến.
    """
    text, engine = tp.transcribe_openai(b"RIFFxxxx", "vi", 12.0)

    assert text == "bản chép whisper-1"
    assert engine == "whisper-1"

    sent = fake_httpx.last
    payload_keys = set(sent["data"]) | set(sent["files"])
    assert "prompt" not in payload_keys, "KHÔNG BAO GIỜ mồi từ vựng — xem docstring"
    assert "initial_prompt" not in payload_keys
    # Vế phủ định thứ hai: chuỗi mồi có thể lọt vào dưới một tên khoá khác.
    blob = json.dumps({k: str(v) for k, v in sent["data"].items()}, ensure_ascii=False).lower()
    assert "business analyst" not in blob and "restful" not in blob


def test_payload_openai_gui_dung_model_va_ngon_ngu(fake_httpx):
    tp.transcribe_openai(b"RIFFxxxx", "vi", 1.0)

    sent = fake_httpx.last
    assert sent["url"] == tp.OPENAI_URL
    assert sent["data"]["model"] == "whisper-1"
    assert sent["data"]["language"] == "vi"
    assert sent["headers"]["Authorization"] == "Bearer sk-test"
    assert sent["files"]["file"][0].endswith(".wav"), "tên file quyết định định dạng OpenAI đoán"
    assert sent["files"]["file"][2] == "audio/wav"


def test_ngon_ngu_rong_thi_KHONG_gui_khoa_language(fake_httpx):
    """API từ chối `language=""` — gửi khoá rỗng biến "để tự dò" thành lỗi 400."""
    tp.transcribe_openai(b"RIFFxxxx", None, 1.0)
    assert "language" not in fake_httpx.last["data"]


def test_ban_chep_rong_tu_openai_thi_NEM_de_roi_ve_cuc_bo(monkeypatch, fake_httpx):
    class _Empty(_FakeClient):
        def post(self, *a, **k):
            return _FakeResp({"text": "   "})

    import httpx
    monkeypatch.setattr(httpx, "Client", _Empty)

    from app import usage
    charged = []
    monkeypatch.setattr(usage, "report_audio_usage", lambda *args: charged.append(args))

    with pytest.raises(ValueError):
        tp.transcribe_openai(b"RIFFxxxx", "vi", 1.0)
    assert charged == [("transcribe", app_settings.openai_transcribe_model, 1.0)]


# ── 5b. Gemini — audio là DỮ LIỆU, lệnh là "chép nguyên văn" ─────────────────────────
class _FakeGeminiClient:
    last: dict = {}

    class models:
        @staticmethod
        def generate_content(model=None, contents=None, config=None):
            _FakeGeminiClient.last.update(model=model, contents=contents, config=config)

            class _R:
                text = "  bản chép gemini  "
                usage_metadata = None

            return _R()


@pytest.fixture
def fake_gemini(monkeypatch):
    _FakeGeminiClient.last = {}
    monkeypatch.setattr(tp, "_get_gemini_client", lambda: _FakeGeminiClient)
    monkeypatch.setattr(app_settings, "usage_sink_base", "")
    monkeypatch.setattr(app_settings, "gemini_model", "gemini-2.5-flash")
    return _FakeGeminiClient


def test_gemini_gui_audio_kem_lenh_chep_nguyen_van(fake_gemini):
    text, engine = tp.transcribe_gemini(b"RIFFxxxx", "vi", 5.0)

    assert text == "bản chép gemini"
    assert engine == "gemini-2.5-flash"
    assert fake_gemini.last["config"].temperature == 0.0, "chép lời không được sáng tạo"


def test_lenh_gemini_cam_lam_muot_ban_chep():
    """Gemini là mô hình NGÔN NGỮ nên xu hướng tự nhiên là viết lại cho mượt — mà bản đã làm mượt
    GIẤU MẤT chính thứ đang chấm (câu bỏ lửng, lặp từ, tự sửa lời).

    Khoá từng vế cấm chứ không khoá cả chuỗi: chuỗi có thể được viết lại cho gọn, nhưng bỏ mất
    một vế cấm thì nó bắt đầu biên tập lại mà KHÔNG có gì báo.
    """
    p = tp.GEMINI_TRANSCRIBE_PROMPT.lower()
    assert "nguyên văn" in p
    assert "không sửa ngữ pháp" in p
    assert "không viết lại cho mượt" in p
    assert "không tóm tắt" in p
    assert "từ đệm" in p and "lặp từ" in p
    # KHÔNG mồi từ vựng: liệt kê thuật ngữ trong lệnh là đúng cái bẫy đã làm mất trắng một bài.
    assert "business analyst" not in p and "restful" not in p


def test_gemini_ban_chep_rong_thi_NEM_de_roi_ve_cuc_bo(monkeypatch, fake_gemini):
    class _Empty(_FakeGeminiClient):
        class models:
            @staticmethod
            def generate_content(**k):
                class _R:
                    text = ""
                    usage_metadata = None
                return _R()

    monkeypatch.setattr(tp, "_get_gemini_client", lambda: _Empty)

    with pytest.raises(ValueError):
        tp.transcribe_gemini(b"RIFFxxxx", "vi", 1.0)


# ── 6. Đo chi phí — best-effort, không được kéo đổ đường chính ────────────────────────
async def test_report_audio_usage_nuot_loi_sink(monkeypatch):
    """Sink chết KHÔNG được biến một lượt chép lời thành answer Failed (mất credit — PAY-13)."""
    from app import usage

    monkeypatch.setattr(app_settings, "usage_sink_base", "http://sink-khong-ton-tai:9")
    monkeypatch.setattr(app_settings, "usage_sink_timeout_seconds", 0.01)

    await usage.report_audio_usage("transcribe", "whisper-1", 12.3)   # không được raise


async def test_report_audio_usage_gui_audioSeconds_lam_tron_len(monkeypatch):
    from app import usage

    sent: dict = {}

    class _Session:
        def __init__(self, *a, **k):
            pass

        async def __aenter__(self):
            return self

        async def __aexit__(self, *a):
            return False

        def post(self, url, json=None, headers=None):
            sent.update(url=url, payload=json, headers=headers)

            class _R:
                status = 200

                async def __aenter__(self_inner):
                    return self_inner

                async def __aexit__(self_inner, *a):
                    return False

            return _R()

    import aiohttp
    monkeypatch.setattr(aiohttp, "ClientSession", _Session)
    monkeypatch.setattr(app_settings, "usage_sink_base", "http://payment:8080")

    await usage.report_audio_usage("transcribe", "whisper-1", 12.3)

    assert sent["url"].endswith("/internal/ai-usage")
    assert sent["payload"]["audioSeconds"] == 13, "tính tiền theo phút ⇒ tròn LÊN"
    assert sent["payload"]["model"] == "whisper-1"
    # KHÔNG bịa số token: dòng 0 token không phân biệt được với "không có số liệu".
    assert "promptTokens" not in sent["payload"]


def test_luot_whisper1_LUON_kem_audioSeconds(monkeypatch, fake_httpx):
    """🔴 HỢP ĐỒNG ĐO CHI PHÍ — thiếu khoá này thì chi phí ra 0 đồng mà KHÔNG gì hỏng.

    Phía Payment: `audioSeconds` vắng ⇒ hiểu là "lượt tính theo TOKEN" ⇒ tra bảng giá token của
    `whisper-1` ⇒ nó khai 0 USD/triệu token (vì nó không bán theo token) ⇒ **chi phí 0 đồng, test
    hai bên đều xanh, production im lặng**. Đúng hạng lỗi cả vòng này sinh ra để chặn.
    """
    seen: dict = {}

    async def _spy(operation, model, audio_seconds):
        seen.update(operation=operation, model=model, audio_seconds=audio_seconds)

    from app import usage
    monkeypatch.setattr(usage, "report_audio_usage", _spy)

    tp.transcribe_openai(b"RIFFxxxx", "vi", 12.0)

    assert seen["model"] == "whisper-1"
    assert seen["audio_seconds"] == 12.0


async def test_audioSeconds_bang_0_VAN_phai_gui_khoa(monkeypatch):
    """`0` KHÁC vắng: `0` = "có chép lời, dài 0 giây" (tính theo phút, ra 0đ) · vắng = "không phải
    lượt chép lời" (tính theo token). Bỏ khoá đi khi giá trị rỗng là làm mất đúng phân biệt đó."""
    from app import usage

    sent: dict = {}
    monkeypatch.setattr(app_settings, "usage_sink_base", "")
    monkeypatch.setattr(usage.logger, "info", lambda msg, payload: sent.update(payload))

    await usage.report_audio_usage("transcribe", "whisper-1", 0)

    assert "audioSeconds" in sent, "0 giây vẫn là một lượt chép lời — khoá phải có mặt"
    assert sent["audioSeconds"] == 0


def test_gemini_bao_theo_TOKEN_khong_kem_audioSeconds(monkeypatch, fake_gemini):
    """Gemini bán theo token và trả sẵn `usage_metadata` ⇒ đi đường token cũ. Gửi kèm
    `audioSeconds` ở đây sẽ khiến Payment tính nhầm nó sang đơn giá theo phút."""
    from app import usage

    calls: dict = {"audio": 0, "token": 0}

    async def _audio(*a, **k):
        calls["audio"] += 1

    async def _token(operation, model, response, meta=None):
        calls["token"] += 1
        calls["model"] = model

    monkeypatch.setattr(usage, "report_audio_usage", _audio)
    monkeypatch.setattr(usage, "report_usage", _token)

    tp.transcribe_gemini(b"RIFFxxxx", "vi", 5.0)

    assert calls["token"] == 1 and calls["audio"] == 0
    assert calls["model"] == "gemini-2.5-flash"


def test_duong_cuc_bo_KHONG_bao_cao_luot_nao(monkeypatch):
    """Whisper cục bộ chạy trên máy mình ⇒ không tốn tiền nhà cung cấp ⇒ không có gì để ghi sổ.

    Áp cho CẢ ca dự phòng: gọi từ xa hỏng thì không có bản chép nào dùng được từ nó, nên không
    được ghi một lượt tiêu thụ cho nhà cung cấp đó.
    """
    from app import usage

    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    t, _ = _make(monkeypatch)
    _stub_remote(monkeypatch, boom=RuntimeError("503"))

    def _no_report(*a, **k):
        raise AssertionError("đường cục bộ không được ghi lượt tiêu thụ nào")

    monkeypatch.setattr(usage, "report_audio_usage", _no_report)
    monkeypatch.setattr(usage, "report_usage", _no_report)

    assert t.transcribe_detailed("/tmp/x.webm", "vi").engine == "local:small"


def test_report_blocking_nuot_moi_loi():
    """Cầu nối sync→async: hỏng ở đây cũng chỉ được mất một dòng thống kê."""
    from app import usage

    async def _boom():
        raise RuntimeError("sink toang")

    usage.report_blocking(_boom())   # không được raise


async def test_report_blocking_chay_duoc_trong_dung_hinh_dang_production():
    """Gọi từ một hàm ĐỒNG BỘ nằm trong `asyncio.to_thread` — đúng hình dạng thật của đường chép
    lời (nó chạy trong thread cạnh Whisper, nơi KHÔNG có event loop để `await`).

    Có test riêng vì đây là loại lỗi chạy được trong unit test mà hỏng ở production: nếu ai đó
    đổi `asyncio.run` sang một cách lấy loop "hiện tại", nó sẽ ném đúng trong thread đó.
    """
    import asyncio

    from app import usage

    ran = []

    async def _job():
        ran.append(1)

    def _sync_call_site():
        usage.report_blocking(_job())
        return "ok"

    assert await asyncio.to_thread(_sync_call_site) == "ok"
    assert ran == [1], "coroutine đo đạc phải THẬT SỰ chạy, không phải bị nuốt im lặng"


# ── 7. Cần gạt quay lui `delivery_metrics_source` gặp nhà cung cấp từ xa ──────────────
def test_nguon_moc_whisper_gap_ban_chep_tu_xa_thi_do_bang_VAD(monkeypatch):
    """Hai cấu hình HỢP LỆ gặp nhau không được đẻ ra số 0 giả.

    `delivery_metrics_source="whisper"` là cần gạt quay lui của F11, nhưng bản chép từ xa KHÔNG
    có biên segment nào để quay lui về. Nếu để nguyên thì nó đo trên danh sách RỖNG và cho ra
    "0 lần ngập ngừng" — con số đó đi vào prompt ngay dưới dòng dặn LLM coi chỉ số thời gian là
    bằng chứng đáng tin nhất.
    """
    monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")
    monkeypatch.setattr(app_settings, "delivery_metrics_source", "whisper")
    t, calls = _make(monkeypatch)
    _stub_remote(monkeypatch, "một hai ba bốn năm sáu bảy tám")

    m = t.transcribe_detailed("/tmp/x.webm", "vi").metrics

    assert calls["vad_audio"] is not None, "phải quay sang VAD chứ không đo trên danh sách rỗng"
    assert m is not None and m.pause_count == 1


def test_nguon_moc_whisper_van_dung_segment_khi_chep_cuc_bo(monkeypatch):
    """Vế đối chứng: cần gạt quay lui vẫn phải làm đúng việc của nó ở đường cục bộ."""
    monkeypatch.setattr(app_settings, "delivery_metrics_source", "whisper")
    t, _ = _make(monkeypatch)

    m = t.transcribe_detailed("/tmp/x.webm", "vi").metrics

    # Segment cục bộ: nói 0-5s, khe 0,5s (dưới ngưỡng 0,7s), nói 5,5-6s ⇒ 0 lần ngập ngừng.
    assert m.pause_count == 0
    assert m.speech_sec == pytest.approx(5.5)
