# app/fluency.py
"""F11 (FR06) — Đo ĐỘ TRÔI CHẢY từ tín hiệu thời gian của audio + đếm từ đệm.

VÌ SAO CÓ FILE NÀY: trước F11, `transcriber.transcribe()` lấy `segments` rồi **vứt sạch**
mốc thời gian, chỉ giữ text (transcriber.py cũ, dòng 23). Nghĩa là mọi thứ thuộc về *cách nói*
— nói nhanh hay chậm, ngắc ngứ hay liền mạch, im lặng bao lâu — đều bị ném đi trước khi tới
bộ chấm. Chấm "độ trôi chảy" bằng cách đưa mỗi text cho LLM = đoán, không phải đo.

THIẾT KẾ — 3 điểm cần biết trước khi sửa file này:

1. **CHỈ dùng mốc thời gian mức SEGMENT, KHÔNG bật `word_timestamps`.**
   `model.transcribe()` trả `segment.start/.end` **sẵn có, không tốn thêm gì**; còn
   `word_timestamps=True` chạy thêm một lượt căn chỉnh bằng cross-attention + DTW
   (xác nhận qua doc faster-whisper) ⇒ chậm hơn. Mà đường `/decide-next` transcribe **ĐỒNG BỘ
   ngay trong request upload** (INT-17) và deploy đang chạy Whisper `small` chính vì lý do
   độ trễ đó. Mốc mức segment đủ để đo tốc độ nói + khoảng lặng ⇒ không đánh đổi độ trễ
   của đường đồng bộ để lấy thứ không cần.

2. **Chỉ số THỜI GIAN đáng tin hơn số ĐẾM TỪ ĐỆM.** Whisper được huấn luyện trên transcript
   đã được làm sạch nên nó **thường xuyên nuốt bớt từ đệm** ("ừm", "ờ") — xem §Whisper nuốt
   từ đệm bên dưới. Nhưng một tiếng "ừm" bị nuốt vẫn **chiếm thời gian thật**, nên nó lộ ra ở
   chỗ khác: khoảng lặng dài hơn / tốc độ nói (từ mỗi phút) thấp đi. Vì vậy
   `fillerCount == 0` **KHÔNG được đọc là "nói rất trôi chảy"** — nó chỉ có nghĩa là *bộ nhận
   dạng không ghi lại từ đệm nào*. Prompt chấm nói rõ điều này (prompts.py).

3. **Hàm ở đây THUẦN** (không I/O, không phụ thuộc Whisper) → test được thẳng bằng segment
   giả, không cần model hay audio thật (conftest.py vốn stub `faster_whisper`).

§Whisper nuốt từ đệm — vì sao danh sách dưới đây cố tình HẸP:
    Danh sách này là **phán đoán có căn cứ, KHÔNG phải lấy từ corpus tiếng Việt có kiểm chứng**
    (repo không có corpus nào; không có thư viện DSP/VAD nào). Vì đằng nào ASR cũng làm số đếm
    thấp hơn thực tế, thêm từ mơ hồ vào danh sách chỉ đổi "đếm hụt" thành "đếm sai" — tệ hơn.
    Nên nguyên tắc: **thà bỏ sót còn hơn buộc tội oan.**
      • CÓ đếm: tiếng ngập ngừng thuần tuý ("ừm", "ờ", "ưm"…) + vài tật nói rõ rệt
        ("kiểu như", "đại loại là"…) — những thứ gần như không bao giờ mang nghĩa.
      • KHÔNG đếm (cố ý): "tức là", "nghĩa là", "ví dụ như", "thứ nhất là" — đây là liên từ
        giải thích HỢP LỆ; người trả lời tốt dùng chúng để cấu trúc câu. Đếm chúng là trừ điểm
        đúng người đang trình bày mạch lạc.
"""
from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field

# Ngưỡng coi một khoảng trống giữa 2 segment là "khoảng lặng đáng kể" (giây).
# 0.7s ≈ dài hơn nhịp ngắt câu tự nhiên, đủ để nghe ra là đang ngập ngừng/nghĩ.
PAUSE_THRESHOLD_SEC = 0.7

# Phiên bản THƯỚC ĐO — tăng khi cách tính đổi tới mức số cũ và số mới KHÔNG SO SÁNH ĐƯỢC.
#   1 = mốc thời gian lấy từ biên segment Whisper (tới 2026-08-05)
#   2 = mốc thời gian lấy từ vùng tiếng nói do VAD xác định
#
# Vì sao cần con dấu: điểm chấm được đem SO SÁNH với nhau (xếp hạng B2B - CAMP-10, đo cải
# thiện của roadmap - BC15). Thước đo đổi giữa chừng mà không đánh dấu thì hai con số sinh ra
# từ hai thước khác nhau vẫn bị đặt cạnh nhau như thể cùng đơn vị.
#
# ⚠ KHÔNG tái dùng được `answer_scores.prompt_version`: giá trị đó do prompt registry của
# InterviewService (F21) cấp, AIService chỉ echo lại — đổi cách TÍNH ở đây không làm nó nhúc
# nhích, nên nó không thể đại diện cho thay đổi này.
DELIVERY_METRICS_VERSION_BY_LANG = {"vi": 2, "en": 3}

# Tiếng ngập ngừng thuần tuý — gần như không bao giờ mang nghĩa trong câu trả lời phỏng vấn.
HESITATION_FILLERS: tuple[str, ...] = (
    "ừm", "ưm", "ừ", "ờ", "ơ", "à ờ", "ừ thì", "ờ thì",
    "hmm", "hm", "um", "uh", "ehm", "eh",
)
HESITATION_FILLERS_EN: tuple[str, ...] = ("er", "erm", "ah", "mm", "mhm", "uhm")

# Tật nói (verbal tic) — mang nghĩa rất mỏng, lặp nhiều là dấu hiệu thiếu trôi chảy.
TIC_FILLERS: tuple[str, ...] = (
    "kiểu như", "kiểu là", "đại loại là", "đại loại thì",
    "nói chung là", "cái gì đó", "gì đó", "thế nào nhỉ", "sao ấy nhỉ",
    "cái mà", "thì cái", "you know", "like là",
)
TIC_FILLERS_EN: tuple[str, ...] = ("you know", "i mean", "sort of", "kind of", "you know what i mean")

# Thứ tự QUAN TRỌNG: cụm dài trước cụm ngắn ("à ờ" phải khớp trước "ờ", "kiểu như" trước "kiểu
# là"), nếu không cụm ngắn ăn mất cụm dài và số đếm sai.
ALL_FILLERS: tuple[str, ...] = tuple(
    sorted(HESITATION_FILLERS + TIC_FILLERS + HESITATION_FILLERS_EN + TIC_FILLERS_EN, key=len, reverse=True)
)


@dataclass
class Segment:
    """Lát cắt Whisper trả về — chỉ giữ 3 thứ ta cần (start/end/text)."""
    start: float
    end: float
    text: str


@dataclass
class DeliveryMetrics:
    """Chỉ số CÁCH NÓI đo được từ audio (F11). Đơn vị: giây, từ/phút, tỉ lệ [0,1]."""
    audio_sec: float = 0.0          # tổng độ dài audio
    speech_sec: float = 0.0         # tổng thời gian THỰC SỰ có tiếng nói (Σ độ dài segment)
    word_count: int = 0
    speech_rate_wpm: float = 0.0    # từ / phút, tính trên speech_sec (không tính lúc im lặng)
    longest_pause_sec: float = 0.0  # khoảng lặng dài nhất GIỮA hai segment
    pause_count: int = 0            # số khoảng lặng > PAUSE_THRESHOLD_SEC
    silence_ratio: float = 0.0      # (audio_sec - speech_sec) / audio_sec
    filler_count: int = 0
    filler_per_100_words: float = 0.0
    filler_breakdown: dict[str, int] = field(default_factory=dict)
    metrics_version: int = 2

    def to_dict(self) -> dict:
        """camelCase — khớp hợp đồng JSON với .NET (DeliveryMetricsDto)."""
        return {
            # Con dấu thước đo — đi kèm chính bộ số nó mô tả, để phía .NET lưu cạnh các cột
            # chỉ số. Dòng khuyết con dấu = đo bằng thước cũ (xem DELIVERY_METRICS_VERSION).
            "metricsVersion": self.metrics_version,
            "audioSec": round(self.audio_sec, 2),
            "speechSec": round(self.speech_sec, 2),
            "wordCount": self.word_count,
            "speechRateWpm": round(self.speech_rate_wpm, 1),
            "longestPauseSec": round(self.longest_pause_sec, 2),
            "pauseCount": self.pause_count,
            "silenceRatio": round(self.silence_ratio, 3),
            "fillerCount": self.filler_count,
            "fillerPer100Words": round(self.filler_per_100_words, 2),
            "fillerBreakdown": dict(self.filler_breakdown),
        }


def _normalize(text: str) -> str:
    """Chuẩn hoá để so khớp từ đệm: NFC + thường + gộp khoảng trắng + bỏ dấu câu.

    Bỏ dấu câu vì Whisper chấm câu rất tuỳ hứng ("ừm, thì" / "ừm thì") — nếu không bỏ thì
    cùng một tiếng ngập ngừng lúc đếm được lúc không, tuỳ bộ nhận dạng đặt dấu phẩy ở đâu.
    """
    text = unicodedata.normalize("NFC", text).lower()
    text = re.sub(r"[.,!?;:…\"'“”‘’()\[\]–—-]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def count_fillers(text: str) -> tuple[int, dict[str, int]]:
    """Đếm từ đệm trong transcript. Trả (tổng, {từ đệm: số lần}).

    Khớp theo BIÊN TỪ (`\\b` không dùng được với tiếng Việt có dấu ở mọi engine, nên tự chặn
    hai đầu bằng lookaround khoảng trắng) → "ừ" KHÔNG khớp bên trong "ừng hộ", và "à" trong
    "cà phê" không bị tính.

    Cụm đã khớp bị **thay bằng khoảng trắng** trước khi khớp cụm ngắn hơn, nên "à ờ" tính là
    MỘT lần "à ờ" chứ không phải vừa "à ờ" vừa "ờ" (nếu không thì cụm dài luôn bị đếm hai lần).
    """
    if not text or not text.strip():
        return 0, {}

    haystack = _normalize(text)
    breakdown: dict[str, int] = {}
    total = 0

    for filler in ALL_FILLERS:
        pattern = re.compile(rf"(?<!\S){re.escape(filler)}(?!\S)")
        matches = pattern.findall(haystack)
        if matches:
            breakdown[filler] = len(matches)
            total += len(matches)
            # Xoá chỗ đã khớp để cụm ngắn hơn không đếm chồng lên cụm dài.
            haystack = pattern.sub(" ", haystack)

    return total, breakdown


def count_words(text: str) -> int:
    """Đếm từ (tách theo khoảng trắng sau chuẩn hoá).

    ⚠ Tiếng Việt là ngôn ngữ ĐƠN ÂM TIẾT khi viết: "học sinh" = 2 âm tiết, 1 từ. Ở đây đếm
    theo ÂM TIẾT (tách theo khoảng trắng) — cố ý, vì tốc độ nói đo bằng âm tiết/phút mới phản
    ánh đúng nhịp nói. Hệ quả: ngưỡng WPM dưới đây là ngưỡng ÂM TIẾT/phút, KHÔNG so sánh
    trực tiếp được với chuẩn "words per minute" của tiếng Anh.
    """
    normalized = _normalize(text)
    return len(normalized.split()) if normalized else 0


def compute_delivery_metrics(
    text: str,
    segments: list[Segment],
    audio_sec: float | None = None,
    language: str | None = "vi",
) -> DeliveryMetrics | None:
    """Tính chỉ số cách nói từ transcript + mốc thời gian segment.

    Trả **None** khi không đủ dữ liệu để đo (không segment nào, hoặc tổng thời lượng nói = 0):
    thà KHÔNG có chỉ số còn hơn có chỉ số bịa. `None` chảy suốt xuống prompt/DB dưới dạng
    "chưa đo được", và bộ chấm được dặn chấm bằng bằng chứng trong transcript thay vì đoán số.
    """
    if not segments:
        return None

    ordered = sorted(segments, key=lambda s: s.start)
    speech_sec = sum(max(0.0, s.end - s.start) for s in ordered)
    if speech_sec <= 0:
        return None

    # Khoảng lặng = khoảng trống GIỮA hai segment liền nhau. Whisper cắt segment ở chỗ ngắt
    # hơi, nên các khoảng trống này chính là chỗ ứng viên dừng lại.
    gaps = [
        max(0.0, nxt.start - cur.end)
        for cur, nxt in zip(ordered, ordered[1:])
    ]

    total_audio = audio_sec if audio_sec and audio_sec > 0 else ordered[-1].end
    total_audio = max(total_audio, speech_sec)   # phòng audio_sec sai/ngắn hơn tổng segment

    word_count = count_words(text)
    filler_count, breakdown = count_fillers(text)

    return DeliveryMetrics(
        audio_sec=total_audio,
        speech_sec=speech_sec,
        word_count=word_count,
        speech_rate_wpm=word_count / (speech_sec / 60.0) if speech_sec > 0 else 0.0,
        longest_pause_sec=max(gaps) if gaps else 0.0,
        pause_count=sum(1 for g in gaps if g > PAUSE_THRESHOLD_SEC),
        silence_ratio=(total_audio - speech_sec) / total_audio if total_audio > 0 else 0.0,
        filler_count=filler_count,
        filler_per_100_words=(filler_count / word_count * 100.0) if word_count else 0.0,
        filler_breakdown=breakdown,
        metrics_version=DELIVERY_METRICS_VERSION_BY_LANG.get(language or "vi", 2),
    )
