# tests/test_silence_gate.py — cổng im lặng + kiểm rác cho nhánh Whisper cục bộ.
#
# VÌ SAO CÓ FILE NÀY (sự cố prod 2026-08-15, session 39834dbb): một bản ghi im lặng 8 giây đi
# trọn đường và sinh ra ĐIỂM SỐ THẬT cho một câu trả lời KHÔNG TỒN TẠI. Chuỗi nhân quả:
#
#   1. `whisper-1` chép sự im lặng thành "Hãy subscribe cho kênh Ghiền Mì Gõ…" (vết bẩn dữ liệu
#      huấn luyện của Whisper trên audio không có tiếng người);
#   2. `looks_broken` BẮT ĐƯỢC (log prod có dòng đó) → rơi về Whisper cục bộ — guard chạy ĐÚNG;
#   3. Whisper cục bộ đẻ ra ĐÚNG chuỗi rác ấy, và **nhánh cục bộ không ai kiểm** → đi thẳng vào
#      bộ chấm → 5 tiêu chí đều 0.0 kèm reasoning trích nguyên câu quảng cáo.
#
# Guard cũ canh cửa TRƯỚC trong khi hai cửa mở ra cùng một phòng. Hai lớp vá, hai lớp test:
#   • cổng im lặng: VAD không thấy tiếng người ⇒ KHÔNG chép lời (và không gọi engine nào);
#   • `looks_broken` áp cho CẢ nhánh dự phòng cục bộ.
import pytest

from app import transcriber as transcriber_mod
from app.config import settings as app_settings
from app.transcriber import JUNK_TRANSCRIPT, NO_SPEECH, SAMPLE_RATE, Transcriber

# Chuỗi rác THẬT đã quan sát trên prod — dùng nguyên văn để test nói đúng ca đã xảy ra.
JUNK_PROD = ("Cảm ơn các bạn for watching this video. Hãy subscribe cho kênh "
             "Ghiền Mì Gõ Để không bỏ lỡ những video hấp dẫn")

_SPEECH_SPANS = [{"start": 0, "end": 3 * SAMPLE_RATE}]


class _Seg:
    def __init__(self, start, end, text):
        self.start, self.end, self.text = start, end, text


def _make(monkeypatch, *, vad_spans, local_text="tôi làm backend ba năm", remote=None):
    """Transcriber đã chặn mọi cửa I/O. `remote=None` ⇒ chạy thuần cục bộ."""
    calls = {"vad": 0, "whisper": 0, "remote": 0}

    monkeypatch.setattr(transcriber_mod, "decode_audio",
                        lambda *a, **k: [0.0] * int(6.0 * SAMPLE_RATE))

    def _vad(audio, vad_options=None, **kwargs):
        calls["vad"] += 1
        return vad_spans

    monkeypatch.setattr(transcriber_mod, "get_speech_timestamps", _vad)

    if remote is None:
        monkeypatch.setattr(app_settings, "transcribe_provider", "local")
    else:
        monkeypatch.setattr(app_settings, "transcribe_provider", "whisper-1")

        def _remote(provider, payload, language, audio_sec, filename=None):
            calls["remote"] += 1
            return remote, provider

        monkeypatch.setattr(transcriber_mod, "transcribe_remote", _remote)

    class _Model:
        def transcribe(self, audio, **kwargs):
            calls["whisper"] += 1
            return [_Seg(0.0, 3.0, local_text)], None

    t = Transcriber()
    t._model_instance = _Model()
    return t, calls


def test_khong_co_tieng_noi_thi_tu_choi_va_khong_goi_engine_nao(monkeypatch):
    """Ca prod 2026-08-15: im lặng ⇒ từ chối, KHÔNG chép lời.

    Vế "không gọi engine nào" quan trọng ngang vế từ chối: nó là thứ biến cổng này thành khoản
    TIẾT KIỆM (không tốn lượt API cho sự im lặng) chứ không phải một lớp lọc dán thêm ở cuối.
    """
    monkeypatch.setattr(app_settings, "silence_gate_enabled", True)
    t, calls = _make(monkeypatch, vad_spans=[], remote="bất kỳ thứ gì")

    r = t.transcribe_detailed("/tmp/x.m4a", "vi")

    assert r.reject_reason == NO_SPEECH
    assert r.text == ""
    assert r.metrics is None, "bản chép bị từ chối thì KHÔNG được kèm số đo"
    assert calls["remote"] == 0 and calls["whisper"] == 0


def test_cong_tat_thi_giu_nguyen_hanh_vi_cu(monkeypatch):
    """Cần gạt quay lui phải là cần gạt THẬT, không phải cờ chết."""
    monkeypatch.setattr(app_settings, "silence_gate_enabled", False)
    t, calls = _make(monkeypatch, vad_spans=[])

    r = t.transcribe_detailed("/tmp/x.m4a", "vi")

    assert r.reject_reason is None
    assert r.text == "tôi làm backend ba năm"
    assert calls["whisper"] == 1


def test_vad_chi_chay_mot_lan_khi_co_tieng_noi(monkeypatch):
    """Cổng và F11 hỏi cùng một phép đo ⇒ chạy hai lần là trả tiền gấp đôi cho cùng câu trả lời,
    ngay trên đường ĐỒNG BỘ của /decide-next."""
    monkeypatch.setattr(app_settings, "silence_gate_enabled", True)
    monkeypatch.setattr(app_settings, "delivery_metrics_source", "vad")
    t, calls = _make(monkeypatch, vad_spans=_SPEECH_SPANS)

    t.transcribe_detailed("/tmp/x.m4a", "vi")

    assert calls["vad"] == 1


def test_whisper_cuc_bo_ra_rac_thi_bi_tu_choi(monkeypatch):
    """🔴 ĐÂY LÀ LỖ ĐÃ LỌT TRÊN PROD: nhánh dự phòng cục bộ trước đây không qua cổng kiểm nào."""
    monkeypatch.setattr(app_settings, "silence_gate_enabled", True)
    t, _ = _make(monkeypatch, vad_spans=_SPEECH_SPANS, local_text=JUNK_PROD)

    r = t.transcribe_detailed("/tmp/x.m4a", "vi")

    assert r.reject_reason == JUNK_TRANSCRIPT
    assert r.text == "", "chuỗi rác KHÔNG được đi tiếp — bộ chấm sẽ chấm nó như thật"
    assert r.metrics is None


def test_tu_xa_ra_rac_nhung_cuc_bo_sach_thi_van_dung_ban_cuc_bo(monkeypatch):
    """Đối chứng: bản vá KHÔNG được giết luôn đường dự phòng.

    Thiếu test này thì "từ chối mọi thứ" cũng làm 4 test trên xanh — mà như vậy là mọi lượt nhà
    cung cấp từ xa trục trặc đều thành answer hỏng, tệ hơn hẳn bug đang sửa.
    """
    monkeypatch.setattr(app_settings, "silence_gate_enabled", True)
    t, calls = _make(monkeypatch, vad_spans=_SPEECH_SPANS,
                     local_text="tôi dùng index để tối ưu truy vấn", remote=JUNK_PROD)

    r = t.transcribe_detailed("/tmp/x.m4a", "vi")

    assert r.reject_reason is None
    assert r.text == "tôi dùng index để tối ưu truy vấn"
    assert r.engine.startswith("local:")
    assert calls["remote"] == 1 and calls["whisper"] == 1


def test_ly_do_tu_choi_la_hop_dong_day_voi_dotnet():
    """Chuỗi lý do là HỢP ĐỒNG DÂY, không phải chi tiết nội bộ.

    .NET so sánh đúng chuỗi `"no_speech"` để đánh answer `Skipped` và bỏ publish job chấm. Đổi
    giá trị ở đây mà quên bên kia KHÔNG ném lỗi — nó chỉ lặng lẽ quay về hành vi cũ (chấm sự im
    lặng), đúng lớp bug `focusCriteria`/`metricsVersion` đã xảy ra ba lần trong repo này.
    """
    assert NO_SPEECH == "no_speech"
    assert JUNK_TRANSCRIPT == "junk_transcript"
