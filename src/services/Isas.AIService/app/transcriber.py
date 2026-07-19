# app/transcriber.py
from dataclasses import dataclass

from faster_whisper import WhisperModel

from app.config import settings
from app.fluency import DeliveryMetrics, Segment, compute_delivery_metrics


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
        """Transcribe + đo chỉ số cách nói (F11).

        ⚠ CỐ Ý KHÔNG bật `word_timestamps=True`: mốc mức segment (`seg.start/.end`) đã có sẵn
        **không tốn thêm gì**, còn mốc mức TỪ bắt Whisper chạy thêm một lượt căn chỉnh
        cross-attention + DTW ⇒ chậm hơn hẳn. Đường `/decide-next` transcribe ĐỒNG BỘ trong
        request upload (INT-17), deploy đã phải hạ `large-v3` → `small` vì đúng lý do độ trễ
        đó — không đánh đổi thêm để lấy thứ mà tốc-độ-nói/khoảng-lặng không cần.
        """
        segments, info = self._model.transcribe(
            audio_path,
            language=language,   # None = auto-detect; "vi" cho tiếng Việt
            beam_size=5,
        )
        # segments là generator — phải duyệt để lấy text (và nay giữ luôn mốc thời gian)
        collected = [
            Segment(start=float(seg.start), end=float(seg.end), text=seg.text)
            for seg in segments
        ]
        text = " ".join(s.text.strip() for s in collected).strip()

        # `info` có thể là None (stub test) hoặc thiếu `duration` → rơi về mốc cuối segment.
        audio_sec = getattr(info, "duration", None) if info is not None else None

        return TranscriptionResult(
            text=text,
            metrics=compute_delivery_metrics(text, collected, audio_sec),
        )

    def transcribe(self, audio_path: str, language: str | None = "vi") -> str:
        """Chỉ lấy text — giữ nguyên chữ ký cũ cho call site không cần chỉ số."""
        return self.transcribe_detailed(audio_path, language).text
