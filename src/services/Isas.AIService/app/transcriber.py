# app/transcriber.py
from dataclasses import dataclass

from faster_whisper import WhisperModel
from faster_whisper.audio import decode_audio
from faster_whisper.vad import VadOptions, get_speech_timestamps

from app.config import settings
from app.fluency import DeliveryMetrics, Segment, compute_delivery_metrics

SAMPLE_RATE = 16000

# Tham số VAD — KHÔNG dùng mặc định của thư viện, cả hai giá trị dưới đều cần thiết:
#
#   • `min_silence_duration_ms` mặc định là **2000**, tức nó GỘP xuyên qua mọi khoảng lặng ngắn
#     hơn 2 giây — đúng những khoảng lặng mà F11 sinh ra để đếm (ngưỡng của ta là 0,7s).
#   • `speech_pad_ms` mặc định NỚI hai đầu mỗi vùng tiếng nói, tức ăn mòn chính khoảng trống
#     giữa chúng ⇒ mọi khoảng lặng đo được sẽ ngắn hơn thực tế.
#
# Bộ này là bộ đã dùng trong phép đo cho ra các con số ghi ở `config.delivery_metrics_source`.
VAD_OPTIONS = VadOptions(
    threshold=0.5,
    min_speech_duration_ms=100,
    min_silence_duration_ms=200,
    speech_pad_ms=0,
)


@dataclass
class TranscriptionResult:
    """F11 — transcript KÈM chỉ số cách nói đo từ mốc thời gian.

    Trước F11 hàm transcribe chỉ trả `str`: `segments` (đã có sẵn start/end) bị vứt ngay tại
    chỗ nối text, nên mọi tín hiệu về *cách nói* biến mất trước khi tới bộ chấm. Nay giữ lại.
    `metrics is None` = không đo được (audio rỗng / không segment) — xem fluency.py.
    """
    text: str
    metrics: DeliveryMetrics | None = None


class Transcriber:
    def __init__(self) -> None:
        # Model load 1 lần, dùng lại (load lại mỗi request rất chậm)
        self._model = WhisperModel(
            settings.whisper_model,
            device=settings.whisper_device,
            compute_type=settings.whisper_compute_type,
        )

    def transcribe_detailed(
        self, audio_path: str, language: str | None = "vi"
    ) -> TranscriptionResult:
        """Transcribe (Whisper) + đo chỉ số cách nói (VAD).

        **Hai công cụ, hai việc — đây là điểm mấu chốt của file này.** Whisper lo phần CHỮ; mọi
        mốc thời gian dùng để đo khoảng lặng lấy từ VAD.

        Vì sao không dùng biên segment của Whisper (hành vi tới 2026-08-05): Whisper được huấn
        luyện để CHÉP LỜI, nên khi ứng viên ngừng nói mà vẫn còn tiếng thở/tiếng phòng, nó kéo
        dài biên segment xuyên qua chỗ đó. Đo trên 7 ghi âm thật: bắt được **2/21** khoảng lặng.
        `large-v3` cũng 2/21 ⇒ **không phải lỗi model, mà là sai công cụ**. Chi tiết số liệu +
        cách quay lui: `config.delivery_metrics_source`.

        ⚠ Bài đo trên audio TỔNG HỢP (im lặng là số 0 tuyệt đối) cho Whisper điểm gần tuyệt đối
        và **giấu hoàn toàn lỗi này** — đừng dùng audio dựng bằng TTS để kết luận về đo im lặng.

        ⚠ KHÔNG dùng `transcribe(vad_filter=True)`: nhánh đó lọc audio TRƯỚC khi chép lời rồi ánh
        xạ ngược mốc thời gian, tức đổi luôn transcript. Ở đây VAD chỉ để ĐO, transcript giữ
        nguyên như trước bản vá.
        """
        # Giải mã MỘT lần rồi đưa cùng một mảng cho cả hai bên (`transcribe` nhận ndarray). Để
        # mỗi bên tự mở file là mở đường cho chênh lệch đến từ khâu giải mã/resample chứ không
        # phải từ thứ đang đo.
        pcm = decode_audio(audio_path, sampling_rate=SAMPLE_RATE)

        segments, _info = self._model.transcribe(
            pcm,
            language=language,   # None = auto-detect; "vi" cho tiếng Việt
            beam_size=5,
        )
        # segments là generator — phải duyệt để lấy text
        collected = [
            Segment(start=float(seg.start), end=float(seg.end), text=seg.text)
            for seg in segments
        ]
        text = " ".join(s.text.strip() for s in collected).strip()

        # Có mảng trong tay thì đo thẳng, đáng tin hơn `info.duration` (và không phụ thuộc vào
        # việc `info` có tồn tại hay không). 0 = không giải mã được gì → fluency tự rơi về mốc
        # cuối segment.
        audio_sec = len(pcm) / SAMPLE_RATE

        return TranscriptionResult(
            text=text,
            metrics=compute_delivery_metrics(text, self._timing_spans(pcm, collected), audio_sec),
        )

    def _timing_spans(self, pcm, whisper_segments: list[Segment]) -> list[Segment]:
        """Các vùng CÓ TIẾNG NÓI dùng để tính khoảng lặng / tỉ lệ im lặng / tốc độ nói.

        Trả về `Segment` không có text: `compute_delivery_metrics` chỉ đọc `start`/`end` ở đây,
        còn phần chữ (đếm từ, đếm từ đệm) nó lấy từ tham số `text` riêng — nên đổi nguồn mốc
        thời gian KHÔNG cần đụng `fluency.py` một dòng nào.
        """
        if settings.delivery_metrics_source == "whisper":
            return whisper_segments
        return [
            Segment(start=t["start"] / SAMPLE_RATE, end=t["end"] / SAMPLE_RATE, text="")
            for t in get_speech_timestamps(pcm, VAD_OPTIONS, sampling_rate=SAMPLE_RATE)
        ]

    def transcribe(self, audio_path: str, language: str | None = "vi") -> str:
        """Chỉ lấy text — giữ nguyên chữ ký cũ cho call site không cần chỉ số."""
        return self.transcribe_detailed(audio_path, language).text
