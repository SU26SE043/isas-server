# tests/test_fluency_f11.py
"""F11 (FR06) — độ trôi chảy + từ đệm.

Đường đi được canh ở đây, theo thứ tự rủi ro:
  1. Tính toán thuần (fluency.py) — đếm đúng, không đếm oan, không chia cho 0.
  2. Transcriber GIỮ mốc thời gian (trước F11 vứt sạch) và KHÔNG bật word_timestamps.
  3. Prompt mang số đo VÀ mang cảnh báo "ASR nuốt từ đệm" — thiếu cảnh báo thì tính năng
     phản tác dụng (LLM đọc "0 từ đệm" = "hoàn hảo").
  4. Worker: CẢ HAI đường (tĩnh = tự transcribe · thích ứng = nhận số đo sẵn) đều có chỉ số.
     Đây là chỗ dễ hỏng ÂM THẦM nhất — xem test lớp (4).
"""
import pytest

from app.fluency import (
    PAUSE_THRESHOLD_SEC,
    DeliveryMetrics,
    Segment,
    compute_delivery_metrics,
    count_fillers,
    count_words,
)
from app.prompts import build_delivery_block, build_scoring_prompt


def _pcm(seconds: float) -> list[float]:
    """Mảng PCM giả 16 kHz — `transcriber` chỉ cần `len()` để suy ra độ dài audio."""
    return [0.0] * int(seconds * 16000)


# ── (1) Đếm từ đệm ────────────────────────────────────────────────────────────────────
def test_dem_tu_dem_co_ban():
    total, breakdown = count_fillers("Ừm, tôi nghĩ là ờ cái này kiểu như hơi khó")
    assert breakdown["ừm"] == 1
    assert breakdown["ờ"] == 1
    assert breakdown["kiểu như"] == 1
    assert total == 3


def test_cum_dai_khong_bi_dem_hai_lan():
    """"à ờ" phải tính MỘT lần cụm dài, không phải vừa "à ờ" vừa "ờ"."""
    total, breakdown = count_fillers("à ờ tôi chưa rõ")
    assert breakdown == {"à ờ": 1}
    assert total == 1


def test_khong_dem_khi_nam_trong_tu_khac():
    """Khớp theo biên từ: "ừ" trong "ừng hộ", "ờ" trong "bờ biển" KHÔNG phải từ đệm."""
    total, breakdown = count_fillers("chúng tôi ủng hộ phương án ở bờ biển")
    assert total == 0, f"đếm oan: {breakdown}"


def test_lien_tu_giai_thich_hop_le_khong_bi_tinh_la_tu_dem():
    """CỐ Ý loại "tức là"/"nghĩa là"/"ví dụ như" khỏi danh sách.

    Đây là liên từ giải thích hợp lệ — người trả lời TỐT dùng chúng để cấu trúc câu. Đếm
    chúng là trừ điểm đúng người đang trình bày mạch lạc. Nếu ai đó thêm chúng vào danh
    sách, test này phải ĐỎ để buộc cân nhắc lại chứ không lặng lẽ trôi qua.
    """
    total, breakdown = count_fillers(
        "Tức là hệ thống có hai phần. Nghĩa là ta tách ra. Ví dụ như phần đọc.")
    assert total == 0, f"đếm oan liên từ giải thích: {breakdown}"


def test_dem_tu_dem_bo_qua_dau_cau_va_hoa_thuong():
    """Whisper chấm câu tuỳ hứng — "Ừm," và "ừm" phải cùng được đếm."""
    a, _ = count_fillers("Ừm, vâng")
    b, _ = count_fillers("ừm vâng")
    assert a == b == 1


def test_dem_tu_dem_text_rong():
    assert count_fillers("") == (0, {})
    assert count_fillers("   ") == (0, {})


# ── (2) Chỉ số cách nói ───────────────────────────────────────────────────────────────
def test_chi_so_co_ban():
    segs = [Segment(0.0, 2.0, "xin chào tôi tên là An"), Segment(5.0, 7.0, "tôi làm backend")]
    m = compute_delivery_metrics("xin chào tôi tên là An tôi làm backend", segs, audio_sec=8.0)

    assert m is not None
    assert m.speech_sec == pytest.approx(4.0)        # 2 + 2
    assert m.longest_pause_sec == pytest.approx(3.0)  # 5.0 - 2.0
    assert m.pause_count == 1
    assert m.audio_sec == pytest.approx(8.0)
    assert m.silence_ratio == pytest.approx(0.5)      # (8-4)/8
    assert m.word_count == 9
    assert m.speech_rate_wpm == pytest.approx(9 / (4.0 / 60.0))


def test_khoang_lang_ngan_hon_nguong_khong_tinh_la_dung():
    """Ngắt hơi tự nhiên giữa câu KHÔNG phải là ngập ngừng."""
    gap = PAUSE_THRESHOLD_SEC / 2
    segs = [Segment(0.0, 1.0, "a b"), Segment(1.0 + gap, 2.0, "c d")]
    m = compute_delivery_metrics("a b c d", segs, audio_sec=2.0)
    assert m is not None and m.pause_count == 0


def test_khong_do_duoc_tra_none_thay_vi_so_bia():
    """Không segment / thời lượng nói = 0 → None. Thà KHÔNG có số còn hơn có số bịa:
    số 0 chảy xuống prompt sẽ bị đọc thành "nói 0 từ/phút" = ngắc ngứ tột độ."""
    assert compute_delivery_metrics("gì đó", [], audio_sec=5.0) is None
    assert compute_delivery_metrics("gì đó", [Segment(1.0, 1.0, "x")], audio_sec=5.0) is None


def test_audio_sec_thieu_thi_lay_moc_cuoi_segment():
    segs = [Segment(0.0, 3.0, "một hai ba")]
    m = compute_delivery_metrics("một hai ba", segs, audio_sec=None)
    assert m is not None and m.audio_sec == pytest.approx(3.0)


def test_audio_sec_ngan_hon_tong_segment_khong_ra_ti_le_am():
    """Phòng info.duration lệch — silence_ratio không được âm."""
    segs = [Segment(0.0, 10.0, "một hai")]
    m = compute_delivery_metrics("một hai", segs, audio_sec=2.0)
    assert m is not None and m.silence_ratio >= 0.0


def test_to_dict_camelcase_khop_hop_dong_dotnet():
    m = DeliveryMetrics(speech_rate_wpm=200.0, filler_count=3, filler_breakdown={"ừm": 3})
    d = m.to_dict()
    assert d["speechRateWpm"] == 200.0
    assert d["fillerCount"] == 3
    assert d["fillerBreakdown"] == {"ừm": 3}
    # Con dấu thước đo đi CÙNG bộ số nó mô tả — thiếu nó thì phía .NET không phân biệt được
    # điểm chấm bằng thước cũ (segment Whisper) với thước mới (VAD), mà hai bộ số đó vẫn bị
    # đem so với nhau ở xếp hạng B2B và ở phần đo cải thiện của roadmap.
    assert d["metricsVersion"] == 2

    # Không được rơi rớt field nào — .NET map theo đúng bộ khoá này.
    assert set(d) == {
        "metricsVersion",
        "audioSec", "speechSec", "wordCount", "speechRateWpm", "longestPauseSec",
        "pauseCount", "silenceRatio", "fillerCount", "fillerPer100Words", "fillerBreakdown",
    }


def test_schema_decide_next_khai_DU_moi_khoa_cua_to_dict():
    """🔴 Bất biến chống một LỚP bug, không phải một field.

    `/decide-next` trả `deliveryMetrics` qua model pydantic `schemas.DeliveryMetrics`. Pydantic
    mặc định `extra='ignore'` ⇒ khoá nào `to_dict()` sinh ra mà schema KHÔNG khai thì bị **nuốt
    im lặng**: không lỗi, không cảnh báo, chỉ là field rụng mất giữa đường.

    Đã xảy ra THẬT hai lần: `focusCriteria` (BC14) và `metricsVersion` (2026-08-05 — thêm vào
    `to_dict()` nhưng quên khai ở schema ⇒ `practice_answers.metrics_version` NULL trên
    production trong khi 362 test đều xanh; chỉ e2e mới bắt được).

    Test này so HAI BỘ KHOÁ thay vì liệt kê tay, nên field thêm về sau tự được bảo vệ.
    """
    from app.schemas import DeliveryMetrics as DeliveryMetricsSchema

    emitted = set(compute_delivery_metrics(
        "một hai ba", [Segment(0.0, 2.0, "một hai ba")], audio_sec=4.0).to_dict())
    declared = set(DeliveryMetricsSchema.model_fields)

    missing = emitted - declared
    assert not missing, (
        f"schemas.DeliveryMetrics thiếu khoá {sorted(missing)} — pydantic sẽ NUỐT IM LẶNG chúng "
        f"trên đường /decide-next. Khai thêm vào schema, đừng bỏ khỏi to_dict()."
    )


# ── (3) Prompt ────────────────────────────────────────────────────────────────────────
def _criteria():
    return [{"criterionId": "c1", "name": "Độ trôi chảy", "maxScore": 5,
             "levels": [{"score": 0, "descriptor": "kém"}, {"score": 5, "descriptor": "tốt"}]}]


def test_prompt_co_so_do_khi_do_duoc():
    m = compute_delivery_metrics(
        "ừm tôi nghĩ vậy", [Segment(0.0, 2.0, "ừm tôi nghĩ vậy")], audio_sec=4.0)
    prompt = build_scoring_prompt("Câu hỏi?", "ừm tôi nghĩ vậy", "BE", _criteria(), m.to_dict())

    assert "CHỈ SỐ TRÌNH BÀY" in prompt
    assert "âm tiết/phút" in prompt
    assert '"ừm" ×1' in prompt


def test_prompt_canh_bao_asr_nuot_tu_dem():
    """Chỉ thị QUAN TRỌNG NHẤT của F11.

    Whisper nuốt bớt từ đệm ⇒ số đếm luôn thấp hơn thực tế. Không có cảnh báo này thì LLM
    đọc "0 từ đệm" thành "nói hoàn hảo" và cho điểm tối đa cho người ngắc ngứ nhất — tính
    năng chấm trôi chảy chạy NGƯỢC mục tiêu mà vẫn xanh mọi test khác.
    """
    m = compute_delivery_metrics("tôi nghĩ vậy", [Segment(0.0, 2.0, "tôi nghĩ vậy")], 2.0)
    prompt = build_scoring_prompt("Câu hỏi?", "tôi nghĩ vậy", "BE", _criteria(), m.to_dict())

    assert "TỰ BỎ BỚT" in prompt
    assert "TỐI THIỂU" in prompt
    assert "ĐÁNG TIN NHẤT" in prompt   # ưu tiên chỉ số thời gian hơn số đếm


def test_prompt_tach_bach_toc_do_noi_voi_ngap_ngung():
    """Bản vá VAD (2026-08-05) đổi Ý NGHĨA của "tốc độ nói" — prompt phải nói ra điều đó.

    Trước bản vá, `speech_sec` bị phồng (kéo dài xuyên khoảng lặng) nên người ngập ngừng có
    tốc độ nói thấp bất thường, và LLM đọc "chậm = ngắc ngứ" — ĐÚNG một cách tình cờ. Đo trên
    7 ghi âm thật: thước cũ gắn nhãn "chậm" cho 4/7, trong đó có một người KHÔNG ngập ngừng
    lần nào (buộc tội oan); sau bản vá còn đúng 1/7 và đó là người ngập ngừng thật.

    Nay `speech_sec` chỉ tính lúc THẬT SỰ có tiếng nói ⇒ tốc độ nói = nhịp phát âm thuần, và
    bằng chứng ngập ngừng chuyển hẳn sang pauseCount/silenceRatio. Không nói rõ thì LLM diễn
    giải con số mới bằng mô hình tư duy cũ và cộng dồn hai tín hiệu độc lập thành một.
    """
    m = compute_delivery_metrics("tôi nghĩ vậy", [Segment(0.0, 2.0, "tôi nghĩ vậy")], 6.0)
    prompt = build_scoring_prompt("Câu hỏi?", "tôi nghĩ vậy", "BE", _criteria(), m.to_dict())

    assert "KHÔNG nằm ở tốc độ nói" in prompt, \
        "phải chỉ rõ bằng chứng ngập ngừng nằm ở khoảng lặng, không ở tốc độ nói"
    assert "đã LOẠI thời gian im lặng ra khỏi mẫu số" in prompt
    assert "đừng cộng dồn chúng thành một lời nhận xét" in prompt


def test_prompt_khong_do_duoc_thi_cam_bia_so():
    block = build_delivery_block(None)
    assert "KHÔNG đo được" in block
    assert "KHÔNG bịa" in block


def test_prompt_cam_dung_chi_so_de_cham_tieu_chi_noi_dung():
    """Nói chậm ≠ kiến thức kém. Thiếu rào này thì chỉ số trình bày kéo tụt điểm chuyên môn."""
    m = compute_delivery_metrics("a b c", [Segment(0.0, 2.0, "a b c")], 2.0)
    assert "KHÔNG dùng chúng để tăng/giảm điểm các tiêu chí về NỘI DUNG" \
        in build_delivery_block(m.to_dict())


def test_prompt_field_khuyet_ghi_chua_do_duoc_khong_in_so_0():
    """Vá 2026-07-19 — field khuyết KHÔNG được in ra là 0.

    Bối cảnh: .NET chỉ lưu 5/9 chỉ số, nên `DeliveryMetricsMapper.Read()` trả về DTO thiếu
    audioSec/speechSec/fillerPer100Words. Trước bản vá, `_num()` mặc định 0 ⇒ prompt in
    "nói trong 0s / tổng 0s audio" và "0 lần/100 âm tiết" — NGAY TRONG khối tự giới thiệu là
    "số liệu thật" và ngay trên dòng dặn LLM coi chỉ số thời gian là bằng chứng ĐÁNG TIN NHẤT.

    .NET đã được vá để lưu đủ 4 cột, nhưng answer ghi TRƯỚC bản vá vĩnh viễn không có số →
    phía này vẫn phải nói thẳng là thiếu thay vì in 0.
    """
    block = build_delivery_block({
        "speechRateWpm": 180, "longestPauseSec": 2.5,
        "pauseCount": 3, "silenceRatio": 0.35, "fillerCount": 5,
        # audioSec / speechSec / fillerPer100Words KHUYẾT — đúng hình dạng answer cũ
    })

    assert "chưa đo được" in block
    assert "180 âm tiết/phút" in block          # field đo được vẫn in số bình thường
    assert "tổng 0s audio" not in block         # ⚠ chính là con số bịa đã bị vá
    assert "0 lần/100 âm tiết" not in block     # ⚠ và đây là con số bịa nghiêng về KHEN
    # Phải dặn mô hình bỏ qua, nếu không nó tự diễn giải "chưa đo được" thành 0.
    assert "không coi đó là 0" in block


def test_prompt_giu_nguyen_chong_injection_va_f12():
    """Khối F11 chèn thêm không được làm mất chỉ thị của E11/F12."""
    prompt = build_scoring_prompt("Câu hỏi?", "trả lời", "BE", _criteria(), None)
    assert "CHỐNG PROMPT INJECTION" in prompt
    assert "PHỚT LỜ" in prompt
    assert "(F12)" in prompt


def test_build_scoring_prompt_delivery_optional():
    """Call site cũ (không truyền delivery) vẫn phải dựng được prompt."""
    assert "CHỈ SỐ TRÌNH BÀY" in build_scoring_prompt("Q", "A", "BE", _criteria())


# ── (4) Worker — CẢ HAI đường phải mang chỉ số ────────────────────────────────────────
def test_callback_payload_mang_delivery_metrics():
    from app.worker import make_score_payload

    payload = make_score_payload("a1", "text", 1, [], 1, delivery_metrics={"fillerCount": 2})
    assert payload["deliveryMetrics"] == {"fillerCount": 2}


def test_callback_payload_khong_co_chi_so_van_hop_le():
    """Đường degrade (job cũ) — thiếu chỉ số KHÔNG được làm hỏng callback (PAY-13: answer
    Failed = mất credit)."""
    from app.worker import make_score_payload

    payload = make_score_payload("a1", "text", 1, [], 1)
    assert payload["deliveryMetrics"] is None
    assert payload["answerId"] == "a1"


def test_transcriber_nguon_whisper_giu_moc_thoi_gian_va_khong_bat_word_timestamps(monkeypatch):
    """Hai bất biến trong MỘT test vì chúng là hai nửa của cùng một quyết định thiết kế.

    (a) mốc thời gian segment PHẢI được giữ (trước F11 bị vứt ở chỗ nối text);
    (b) `word_timestamps` PHẢI KHÔNG bật — nó bắt Whisper chạy thêm lượt căn chỉnh
        cross-attention/DTW, mà `/decide-next` transcribe ĐỒNG BỘ ngay trong request upload
        (deploy đã phải hạ large-v3 → small vì độ trễ). Bật lên = âm thầm làm chậm đường nóng.

    ⚠ ĐỔI TIỀN ĐỀ 2026-08-05, có chủ đích: vế (a) nay mô tả đường **rollback**
    (`delivery_metrics_source="whisper"`) chứ không còn là đường mặc định. Lý do đổi mặc định
    nằm ở `config.delivery_metrics_source` — tóm tắt: biên segment Whisper chỉ bắt được 2/21
    khoảng lặng trên ghi âm thật. Test này vẫn có giá trị vì đường rollback phải THẬT SỰ chạy
    được, chứ không phải một cờ chết. Đường mặc định do `test_delivery_metrics_vad.py` khoá.
    """
    from app import transcriber as transcriber_mod
    from app.config import settings as app_settings
    from app.transcriber import Transcriber

    monkeypatch.setattr(app_settings, "delivery_metrics_source", "whisper")
    # ⚠ ĐỔI TIỀN ĐỀ 2026-08-15: tắt CỔNG IM LẶNG. Test này nói về NGUỒN MỐC THỜI GIAN, và nó dựng
    # PCM tổng hợp mà VAD (đúng đắn) coi là không có tiếng người — cổng bật thì bản chép bị từ chối
    # trước khi tới phần đang đo. Cổng có bộ test riêng (`test_silence_gate.py`).
    monkeypatch.setattr(app_settings, "silence_gate_enabled", False)
    # 6 giây audio: nay độ dài đo từ chính mảng PCM chứ không lấy `info.duration` nữa (có mảng
    # trong tay thì đo thẳng đáng tin hơn). `__len__` là thứ duy nhất transcriber cần.
    monkeypatch.setattr(transcriber_mod, "decode_audio", lambda *a, **k: _pcm(6.0))

    captured = {}

    class _Seg:
        def __init__(self, start, end, text):
            self.start, self.end, self.text = start, end, text

    class _Model:
        def transcribe(self, audio, **kwargs):
            captured.update(kwargs)
            return [_Seg(0.0, 2.0, "ừm tôi là"), _Seg(4.0, 6.0, "kỹ sư backend")], None

    t = Transcriber()      # bỏ qua __init__ (nạp model thật)
    t._model_instance = _Model()

    result = t.transcribe_detailed("/tmp/x.webm", "vi")

    assert captured.get("word_timestamps") in (None, False), \
        "word_timestamps bật lên sẽ làm chậm đường /decide-next đồng bộ"
    assert result.text == "ừm tôi là kỹ sư backend"
    assert result.metrics is not None
    assert result.metrics.longest_pause_sec == pytest.approx(2.0)   # 4.0 - 2.0
    assert result.metrics.filler_count == 1                          # "ừm"
    assert result.metrics.audio_sec == pytest.approx(6.0)            # từ độ dài mảng PCM


def test_transcribe_str_van_tra_text_cho_call_site_cu():
    from app.transcriber import Transcriber

    class _Model:
        def transcribe(self, path, **kwargs):
            return [], None

    t = Transcriber()
    t._model_instance = _Model()
    assert t.transcribe("/tmp/x.webm") == ""
