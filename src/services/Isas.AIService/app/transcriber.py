# app/transcriber.py
import logging
import threading
from pathlib import Path
from dataclasses import dataclass

from faster_whisper import WhisperModel
from faster_whisper.audio import decode_audio
from faster_whisper.vad import VadOptions, get_speech_timestamps

from app.config import settings
from app.fluency import DeliveryMetrics, Segment, compute_delivery_metrics
from app.transcribe_providers import (
    LOCAL, looks_broken, pcm_to_wav_bytes, transcribe_remote,
)

logger = logging.getLogger(__name__)

SAMPLE_RATE = 16000
ORIGINAL_EXTENSIONS = {".webm", ".ogg", ".oga", ".mp3", ".m4a", ".mp4", ".mpeg", ".mpga", ".flac", ".wav"}
OPENAI_MAX_AUDIO_BYTES = 25 * 1024 * 1024

# Lý do từ chối bản chép — HỢP ĐỒNG DÂY với .NET (`AnswerService.NoSpeechReason`,
# `DecideNextResult.RejectReason`). Đổi chuỗi ở đây mà quên bên kia thì .NET bind ra một giá trị
# nó không nhận ra → quay về hành vi cũ (chấm sự im lặng) mà KHÔNG lỗi ở đâu cả.
NO_SPEECH = "no_speech"
JUNK_TRANSCRIPT = "junk_transcript"

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

    ``engine`` = CON DẤU nhà cung cấp đã thật sự chép ra `text` (`"whisper-1"` /
    `"gemini-2.5-flash"` / `"local:small"`). Bắt buộc phải có vì đường này nay có DỰ PHÒNG: khi
    nhà cung cấp từ xa hỏng, bản chép rơi về Whisper cục bộ — chất lượng khác hẳn (lỗi từ 0,7%
    so với 4,2%) mà nhìn từ ngoài thì hai bản không phân biệt được. Không có con dấu thì câu hỏi
    "điểm thấp này do ứng viên hay do bản chép?" là câu không trả lời được.

    ``None`` = chưa đóng dấu (test dựng tay, hoặc bản chép có sẵn từ đường khác) — cố ý phân
    biệt với chuỗi rỗng, theo đúng nếp ``promptVersion`` của BK23: NULL là "không biết", khác
    hẳn một giá trị cụ thể.
    """
    text: str
    metrics: DeliveryMetrics | None = None
    engine: str | None = None

    # Vì sao bản chép này KHÔNG DÙNG ĐƯỢC (``None`` = dùng được, đường thường):
    #   • ``"no_speech"``      — VAD không thấy vùng tiếng nói nào. KHÔNG hề gọi engine nào.
    #   • ``"junk_transcript"``— cả từ xa lẫn cục bộ đều ra chuỗi rác máy sinh (:func:`looks_broken`).
    # Cả hai ca ``text`` đều RỖNG: caller không được nhận một chuỗi trông-như-câu-trả-lời.
    #
    # 🔴 VÌ SAO CẦN: quan sát trên prod 2026-08-15 — bản ghi im lặng 8 giây, `whisper-1` trả
    # "Hãy subscribe cho kênh Ghiền Mì Gõ…", `looks_broken` bắt được và rơi về Whisper cục bộ,
    # rồi CỤC BỘ ĐẺ RA ĐÚNG CHUỖI ĐÓ — nhưng nhánh cục bộ không ai kiểm nên nó đi thẳng vào bộ
    # chấm và sinh ra một điểm số có thật cho một câu trả lời không tồn tại.
    reject_reason: str | None = None


class Transcriber:
    def __init__(self) -> None:
        # Model nạp LƯỜI — dựng ở lần dùng đầu, không phải lúc import.
        #
        # Đo trong image (`ru_maxrss`): python trần 12 MB → +`WhisperModel(small,int8)` = **778 MB**.
        # Cả `main.py` lẫn `worker.py` dựng `Transcriber()` ở module scope ⇒ trước đây là ~1,5 GB
        # nằm thường trú ở hai tiến trình. Trên Mac 32 GB không ai để ý; trên server 8 core/7,6 GB
        # thì đó là phần RAM quyết định có nới được thread pool hay không.
        #
        # Đường từ xa (`TRANSCRIBE_PROVIDER != local`) KHÔNG bao giờ chạm model này, nên ở cấu hình
        # production hiện tại nó chỉ là lưới dự phòng — nhưng là lưới THẬT (xem `_text`: 4xx/chép
        # rỗng/`looks_broken` đều rơi về đây), không phải code chết.
        #
        # ⚠ Đánh đổi: lần rơi về cục bộ ĐẦU TIÊN trả tiền nạp model ngay trong request. Volume
        # HF cache phải được nạp trước khi cắt chuyển, nếu không đó là ~480 MB tải về nằm trong
        # lòng timeout 90s của decider — tức làm một đường vốn đã hỏng chậm thêm.
        self._model_lock = threading.Lock()
        self._model_instance: WhisperModel | None = None

    @property
    def _model(self) -> WhisperModel:
        # Double-checked locking, và khoá ở đây là BẮT BUỘC chứ không phải cho đẹp:
        # `_transcribe_local` chạy trong thread do `asyncio.to_thread` cấp. Hai lượt dự phòng
        # đồng thời mà không khoá sẽ dựng HAI `WhisperModel` ⇒ tải đôi + RAM đôi, đúng lúc hệ
        # đang chịu tải (vì dự phòng chỉ kích hoạt khi nhà cung cấp từ xa đang hỏng).
        if self._model_instance is None:
            with self._model_lock:
                if self._model_instance is None:
                    logger.info("Nạp WhisperModel %s (lần đầu, nạp lười)", settings.whisper_model)
                    self._model_instance = WhisperModel(
                        settings.whisper_model,
                        device=settings.whisper_device,
                        compute_type=settings.whisper_compute_type,
                    )
        return self._model_instance

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
        # phải từ thứ đang đo. Nhà cung cấp TỪ XA cũng nhận đúng mảng này (dựng lại thành WAV),
        # nên bất biến "một bản giải mã" giữ nguyên khi đổi nguồn CHỮ.
        pcm = decode_audio(audio_path, sampling_rate=SAMPLE_RATE)

        # Có mảng trong tay thì đo thẳng, đáng tin hơn `info.duration` (và không phụ thuộc vào
        # việc `info` có tồn tại hay không). 0 = không giải mã được gì → fluency tự rơi về mốc
        # cuối segment.
        audio_sec = len(pcm) / SAMPLE_RATE

        # CỔNG IM LẶNG — chạy TRƯỚC mọi lượt chép lời. Không có vùng tiếng nói nào ⇒ không có gì để
        # chép: trả rỗng kèm lý do, KHÔNG gọi nhà cung cấp nào (tiết kiệm luôn một lượt API).
        #
        # Cố ý hỏi VAD chứ không hỏi `delivery_metrics_source`: cần gạt đó chọn nguồn MỐC THỜI GIAN
        # cho F11, còn "bản ghi này có tiếng người không" thì chỉ VAD trả lời được — biên segment
        # của Whisper trên audio im lặng chính là thứ đang bịa ra câu trả lời.
        # `None` = CHƯA chạy VAD. Giữ lười để cấu hình quay lui `delivery_metrics_source="whisper"`
        # + cổng TẮT vẫn không tốn một lượt VAD nào (hợp đồng cũ). Cổng BẬT thì VAD chạy đúng MỘT
        # lần và `_timing_spans` dùng lại kết quả đó.
        vad_spans: list[Segment] | None = None
        if settings.silence_gate_enabled:
            vad_spans = self._vad_spans(pcm)
            if not vad_spans:
                logger.warning(
                    "Bản ghi %.1fs KHÔNG có vùng tiếng nói nào (VAD) — bỏ chép lời, trả %s",
                    audio_sec, NO_SPEECH)
                return TranscriptionResult(
                    text="", metrics=None, engine=None, reject_reason=NO_SPEECH)

        text, engine, whisper_segments, reject = self._text(pcm, language, audio_sec, audio_path)

        # Bản chép bị từ chối ⇒ KHÔNG kèm chỉ số: số đo của một bản chép không dùng được chỉ làm
        # prompt chấm tin vào thứ không có thật (đúng lớp lỗi F11 đã diệt).
        if reject is not None:
            return TranscriptionResult(text="", metrics=None, engine=engine, reject_reason=reject)

        return TranscriptionResult(
            text=text,
            metrics=compute_delivery_metrics(
                text,
                # Nhà cung cấp từ xa KHÔNG trả biên segment ⇒ cần cờ này để cần gạt quay lui
                # `delivery_metrics_source="whisper"` không âm thầm đo trên danh sách rỗng.
                self._timing_spans(pcm, whisper_segments, vad_spans,
                                   whisper_available=engine.startswith(f"{LOCAL}:")),
                audio_sec, language),
            engine=engine,
        )

    def _text(self, pcm, language: str | None, audio_sec: float,
              audio_path: str) -> tuple[str, str, list[Segment], str | None]:
        """Lấy phần CHỮ. Trả ``(text, engine, whisper_segments, reject_reason)``.

        Nhà cung cấp từ xa không trả biên segment ⇒ ``whisper_segments`` rỗng ở nhánh đó. Mốc
        thời gian KHÔNG đi qua đây — nó luôn là việc của VAD (:meth:`_timing_spans`), nên đổi
        nguồn chữ không đụng được một dòng nào của F11.

        DỰ PHÒNG hai tầng, cố ý không có tầng thứ ba: từ xa hỏng → Whisper cục bộ; cục bộ hỏng
        nốt → để lỗi nổi lên nguyên như trước (worker biến thành ``PermanentError`` → answer
        ``Failed``). Phát minh thêm đường ở đây tức là thêm một trạng thái mà không ai đã đo.
        """
        provider = (settings.transcribe_provider or LOCAL).strip()
        if provider != LOCAL:
            original = self._read_original(audio_path)
            try:
                if original is not None:
                    try:
                        text, engine = transcribe_remote(provider, original[0], language, audio_sec, original[1])
                    except Exception as ex:
                        if not self._should_retry_original_as_wav(ex):
                            raise
                        logger.warning("Nhà cung cấp từ chối file gốc %s — thử lại WAV", original[1], exc_info=True)
                        text, engine = transcribe_remote(provider, pcm_to_wav_bytes(pcm, SAMPLE_RATE), language, audio_sec)
                else:
                    text, engine = transcribe_remote(provider, pcm_to_wav_bytes(pcm, SAMPLE_RATE), language, audio_sec)
                reason = looks_broken(text)
                if reason is None:
                    return text, engine, [], None
                # Bản chép hỏng KHÔNG được đi tiếp trong im lặng: nó vẫn là chuỗi ký tự hợp lệ
                # và bộ chấm sẽ chấm nó như thật. Xem `looks_broken` để biết ca đã quan sát được.
                logger.warning(
                    "Chép lời bằng %s có dấu hiệu hỏng (%s) — dùng lại Whisper cục bộ",
                    provider, reason)
            except Exception:  # noqa: BLE001 — mọi hỏng hóc từ xa đều rơi về cục bộ
                logger.warning(
                    "Chép lời bằng %s hỏng — dùng lại Whisper cục bộ", provider, exc_info=True)

        text, engine, collected = self._transcribe_local(pcm, language)

        # 🔴 Nhánh dự phòng PHẢI qua CÙNG một cổng kiểm với nhánh từ xa. Thiếu vế này chính là lỗ
        # đã lọt trên prod 2026-08-15: `whisper-1` ra chuỗi rác → guard bắt được → rơi về cục bộ →
        # cục bộ ra ĐÚNG chuỗi rác đó → không ai kiểm → vào thẳng bộ chấm. Guard chỉ canh cửa trước
        # trong khi cả hai cửa mở ra cùng một phòng.
        reason = looks_broken(text)
        if reason is not None:
            logger.warning(
                "Whisper cục bộ CŨNG ra bản chép hỏng (%s) — từ chối, không chấm", reason)
            return "", engine, collected, JUNK_TRANSCRIPT

        return text, engine, collected, None

    @staticmethod
    def _should_retry_original_as_wav(error: Exception) -> bool:
        """Chỉ thử WAV khi lỗi cho thấy payload gốc không được chấp nhận.

        Timeout/lỗi mạng retry thêm một lượt sẽ vượt ngân sách 90 giây của decider; lỗi đó phải
        rơi ngay về Whisper cục bộ. ``ValueError`` là bản chép HTTP 200 nhưng rỗng.

        ⚠ Phải phủ **cả hai** nhà cung cấp: `transcribe_openai` dùng httpx (``HTTPStatusError``)
        còn `transcribe_gemini` dùng google-genai (``ClientError`` = 4xx theo định nghĩa của SDK;
        ``ServerError`` = 5xx nên KHÔNG retry, giống 5xx của httpx). Thiếu vế gemini thì đổi
        ``TRANSCRIBE_PROVIDER=gemini`` — một biến env đổi được KHÔNG cần deploy — sẽ làm mọi lượt
        bị từ chối rơi thẳng về Whisper `small` (lỗi từ 0,7% lên 4,2%) trong im lặng.
        """
        if isinstance(error, ValueError):
            return True
        try:
            import httpx
            if isinstance(error, httpx.HTTPStatusError):
                return 400 <= error.response.status_code < 500
        except ImportError:
            pass
        try:
            from google.genai import errors as genai_errors
            return isinstance(error, genai_errors.ClientError)
        except ImportError:
            return False

    @staticmethod
    def _read_original(audio_path: str) -> tuple[bytes, str] | None:
        path = Path(audio_path)
        if (not settings.transcribe_send_original or path.suffix.lower() not in ORIGINAL_EXTENSIONS
                or not path.is_file() or path.stat().st_size > OPENAI_MAX_AUDIO_BYTES):
            return None
        return path.read_bytes(), path.name

    def _transcribe_local(self, pcm, language: str | None) -> tuple[str, str, list[Segment]]:
        """Whisper cục bộ — hành vi y hệt trước vòng này (đường mặc định)."""
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
        return text, f"{LOCAL}:{settings.whisper_model}", collected

    def _vad_spans(self, pcm) -> list[Segment]:
        """Các vùng CÓ TIẾNG NÓI theo VAD. Rỗng ⇔ bản ghi không có tiếng người.

        Tách hàm vì nay có HAI người dùng: cổng im lặng (:meth:`transcribe_detailed`) và nguồn mốc
        thời gian của F11 (:meth:`_timing_spans`) — chạy VAD hai lần trên cùng mảng là trả tiền
        gấp đôi cho cùng một câu trả lời, ngay trên đường ĐỒNG BỘ của `/decide-next`.
        """
        return [
            Segment(start=t["start"] / SAMPLE_RATE, end=t["end"] / SAMPLE_RATE, text="")
            for t in get_speech_timestamps(pcm, VAD_OPTIONS, sampling_rate=SAMPLE_RATE)
        ]

    def _timing_spans(self, pcm, whisper_segments: list[Segment],
                      vad_spans: list[Segment] | None = None,
                      *, whisper_available: bool = True) -> list[Segment]:
        """Các vùng CÓ TIẾNG NÓI dùng để tính khoảng lặng / tỉ lệ im lặng / tốc độ nói.

        Trả về `Segment` không có text: `compute_delivery_metrics` chỉ đọc `start`/`end` ở đây,
        còn phần chữ (đếm từ, đếm từ đệm) nó lấy từ tham số `text` riêng — nên đổi nguồn mốc
        thời gian KHÔNG cần đụng `fluency.py` một dòng nào.

        ``whisper_available=False`` ⇔ phần chữ đến từ nhà cung cấp TỪ XA, tức không có biên
        segment nào để mà quay lui về. Nếu bỏ vế này thì bật `delivery_metrics_source="whisper"`
        CÙNG một nhà cung cấp từ xa sẽ đo trên danh sách RỖNG và cho ra "0 lần ngập ngừng, 0
        giây im lặng" — đúng hạng lỗi F11 sinh ra để diệt (bịa số 0 rồi bảo LLM tin nó nhất),
        chỉ khác là lần này do hai cấu hình hợp lệ gặp nhau.
        """
        if settings.delivery_metrics_source == "whisper":
            if whisper_available:
                return whisper_segments
            logger.warning(
                "delivery_metrics_source='whisper' nhưng bản chép đến từ nhà cung cấp từ xa "
                "(không có biên segment) — đo bằng VAD")
        # `None` = cổng im lặng tắt nên chưa ai chạy VAD → chạy tại đây (đường cũ).
        return vad_spans if vad_spans is not None else self._vad_spans(pcm)

    def transcribe(self, audio_path: str, language: str | None = "vi") -> str:
        """Chỉ lấy text — giữ nguyên chữ ký cũ cho call site không cần chỉ số."""
        return self.transcribe_detailed(audio_path, language).text
