# app/transcriber.py
from faster_whisper import WhisperModel

from app.config import settings


class Transcriber:
    def __init__(self) -> None:
        # Model load 1 lần, dùng lại (load lại mỗi request rất chậm)
        self._model = WhisperModel(
            settings.whisper_model,
            device=settings.whisper_device,
            compute_type=settings.whisper_compute_type,
        )

    def transcribe(self, audio_path: str, language: str | None = "vi") -> str:
        segments, _info = self._model.transcribe(
            audio_path,
            language=language,   # None = auto-detect; "vi" cho tiếng Việt
            beam_size=5,
        )
        # segments là generator — phải duyệt để lấy text
        text = " ".join(seg.text.strip() for seg in segments)
        return text.strip()