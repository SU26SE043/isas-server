# tests/test_delivery_metrics_vad.py — F11 lấy mốc thời gian từ VAD, không từ segment Whisper.
#
# VÌ SAO CÓ FILE NÀY: biên segment của Whisper chỉ bắt được **2/21** khoảng lặng trên 7 ghi âm
# thật (trọng tài: hai bộ dò độc lập tự hiệu chuẩn đạt 0,02-0,03s, đồng ý nhau 18/21). Ca nặng
# nhất là câu trả lời 45s ngập ngừng 7 lần bị báo `pauseCount=0`, `silenceRatio=0,020` trong khi
# thực tế 0,315 — sai 16 lần và LUÔN nghiêng về phía khen ứng viên.
#
# Điểm mấu chốt khi đọc file này: mọi test dưới đây cho Whisper và VAD trả về hình dạng KHÁC
# HẲN NHAU, rồi khẳng định chỉ số bám theo VAD. Nếu hai nguồn trả giống nhau thì test sẽ xanh
# kể cả khi ai đó lặng lẽ đổi ngược về segment Whisper — tức là không khoá được gì.
import pytest

from app import transcriber as transcriber_mod
from app.config import settings as app_settings
from app.transcriber import SAMPLE_RATE, VAD_OPTIONS, Transcriber


class _Seg:
    def __init__(self, start, end, text):
        self.start, self.end, self.text = start, end, text


def _pcm(seconds: float) -> list[float]:
    """Mảng PCM giả — `transcriber` chỉ cần `len()` để suy ra độ dài audio."""
    return [0.0] * int(seconds * SAMPLE_RATE)


# Whisper: gần như nói liên tục, khe duy nhất 0,5s (DƯỚI ngưỡng 0,7s ⇒ không tính là ngập ngừng)
#   ⇒ pauseCount 0 · longestPause 0,5 · speech 5,5/6,0 ⇒ silenceRatio ≈ 0,083
# VAD: nói 0-1s rồi IM 3 GIÂY rồi nói 4-6s
#   ⇒ pauseCount 1 · longestPause 3,0 · speech 3,0/6,0 ⇒ silenceRatio 0,5
# Đây chính là hình dạng lỗi ngoài đời: Whisper kéo dài biên xuyên qua khoảng lặng có tiếng thở.
_WHISPER_SEGS = [_Seg(0.0, 5.0, "tôi từng làm"), _Seg(5.5, 6.0, "dự án đó")]
_VAD_SPANS = [
    {"start": 0, "end": 1 * SAMPLE_RATE},
    {"start": 4 * SAMPLE_RATE, "end": 6 * SAMPLE_RATE},
]


def _make(monkeypatch, *, vad_spans=None, whisper_segs=None, seconds=6.0):
    """Dựng Transcriber đã bị chặn mọi cửa I/O; trả (transcriber, sổ ghi lời gọi)."""
    calls: dict = {"decode": 0, "vad_audio": None, "whisper_audio": None, "vad_options": None}
    pcm = _pcm(seconds)

    def _decode(*args, **kwargs):
        calls["decode"] += 1
        return pcm

    def _vad(audio, vad_options=None, **kwargs):
        calls["vad_audio"] = audio
        calls["vad_options"] = vad_options
        return _VAD_SPANS if vad_spans is None else vad_spans

    monkeypatch.setattr(transcriber_mod, "decode_audio", _decode)
    monkeypatch.setattr(transcriber_mod, "get_speech_timestamps", _vad)

    segs = _WHISPER_SEGS if whisper_segs is None else whisper_segs

    class _Model:
        def transcribe(self, audio, **kwargs):
            calls["whisper_audio"] = audio
            return segs, None

    t = Transcriber()      # bỏ qua __init__ (nạp model thật)
    t._model_instance = _Model()
    return t, calls


def test_chi_so_bam_vung_vad_chu_khong_bam_segment_whisper(monkeypatch):
    """Bất biến TRUNG TÂM của bản vá.

    Hai nguồn cố ý mâu thuẫn nhau: Whisper thấy 0 lần ngập ngừng, VAD thấy một khoảng im 3 giây.
    Chỉ số phải kể câu chuyện của VAD — đó là câu chuyện đúng.
    """
    t, _ = _make(monkeypatch)
    m = t.transcribe_detailed("/tmp/x.webm", "vi").metrics

    assert m is not None
    assert m.pause_count == 1, "khoảng im 3 giây của VAD phải được đếm"
    assert m.longest_pause_sec == pytest.approx(3.0)
    assert m.speech_sec == pytest.approx(3.0), "chỉ tính lúc THẬT SỰ có tiếng nói"
    assert m.silence_ratio == pytest.approx(0.5)

    # Vế PHỦ ĐỊNH — không có nó thì test vẫn xanh khi ai đó đổi ngược về segment Whisper mà
    # tình cờ cho ra con số gần đúng.
    assert m.longest_pause_sec != pytest.approx(0.5), "0,5s là khe của Whisper, không phải VAD"
    assert m.pause_count != 0


def test_phan_chu_van_do_whisper_dam_nhiem(monkeypatch):
    """Đổi nguồn mốc THỜI GIAN không được đụng tới phần CHỮ — đếm từ/từ đệm vẫn từ transcript."""
    t, _ = _make(monkeypatch, whisper_segs=[_Seg(0.0, 5.0, "ừm tôi từng làm dự án đó")])
    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert result.text == "ừm tôi từng làm dự án đó"
    assert result.metrics.filler_count == 1
    # 7 ÂM TIẾT (`count_words` tách theo khoảng trắng — tiếng Việt đơn âm tiết khi viết), và từ
    # đệm "ừm" vẫn nằm trong số đếm: nó là một âm tiết được nói ra thật.
    assert result.metrics.word_count == 7


def test_giai_ma_mot_lan_va_dua_CUNG_mang_cho_ca_hai(monkeypatch):
    """Whisper và VAD phải nhìn ĐÚNG một bản giải mã.

    Để mỗi bên tự mở file là mở đường cho chênh lệch đến từ khâu giải mã/resample chứ không
    phải từ thứ đang đo — và tốn gấp đôi công giải mã trên đường `/decide-next` đồng bộ.
    """
    t, calls = _make(monkeypatch)
    t.transcribe_detailed("/tmp/x.webm", "vi")

    assert calls["decode"] == 1, "giải mã hai lần = phí công trên đường đồng bộ"
    assert calls["whisper_audio"] is calls["vad_audio"], "hai bên phải nhận CÙNG một đối tượng"


def test_rollback_ve_segment_whisper_that_su_chay(monkeypatch):
    """Cờ quay lui phải là cần gạt THẬT, không phải cờ chết.

    Cùng đầu vào như test trung tâm nhưng đổi nguồn → chỉ số phải kể câu chuyện của Whisper.
    """
    monkeypatch.setattr(app_settings, "delivery_metrics_source", "whisper")
    t, calls = _make(monkeypatch)
    m = t.transcribe_detailed("/tmp/x.webm", "vi").metrics

    assert m.pause_count == 0
    assert m.longest_pause_sec == pytest.approx(0.5)
    assert m.speech_sec == pytest.approx(5.5)
    assert calls["vad_audio"] is None, "chế độ whisper thì KHÔNG được gọi VAD (phí thời gian)"


def test_khong_vung_tieng_noi_thi_metrics_None_chu_khong_phai_so_0(monkeypatch):
    """Giữ nguyên hợp đồng của bản vá 2026-07-19: KHÔNG bịa số 0.

    VAD không thấy tiếng nói nào (audio im/hỏng) ⇒ "chưa đo được", để prompt nói thẳng là thiếu.
    Trả 0 ở đây nghĩa là báo "im lặng 0%, không ngập ngừng lần nào" — bịa, và bịa theo hướng khen.
    """
    t, _ = _make(monkeypatch, vad_spans=[])
    assert t.transcribe_detailed("/tmp/x.webm", "vi").metrics is None


def test_tham_so_vad_khong_de_mac_dinh_thu_vien(monkeypatch):
    """Hai giá trị này mà để mặc định thì tính năng hỏng ÂM THẦM.

    `min_silence_duration_ms` mặc định 2000 ⇒ gộp xuyên qua mọi khoảng lặng ngắn hơn 2 giây,
    đúng những khoảng mà F11 sinh ra để đếm (ngưỡng của ta là 0,7s). `speech_pad_ms` mặc định
    nới hai đầu vùng tiếng nói ⇒ ăn mòn chính khoảng trống giữa chúng.
    """
    assert VAD_OPTIONS.min_silence_duration_ms == 200
    assert VAD_OPTIONS.speech_pad_ms == 0

    t, calls = _make(monkeypatch)
    t.transcribe_detailed("/tmp/x.webm", "vi")
    assert calls["vad_options"] is VAD_OPTIONS, "phải truyền bộ tham số của ta, không để None"
