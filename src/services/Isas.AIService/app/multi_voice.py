# app/multi_voice.py — AC1/B5: phát hiện ≥2 giọng nói trong audio một câu trả lời B2B.
#
# THỬ NGHIỆM, mặc định TẮT (`MULTI_VOICE_ENABLED=false`) — theo đúng tiền lệ mọi rollout khác của
# repo (`GROUNDING_ENABLED` · `TIERING_ENABLED` · `CV_SCREENING_ENABLED` đều false).
#
# ĐƯỜNG CHẠY: worker chấm BẤT ĐỒNG BỘ (`app/worker.py`), SAU khi đã có transcript.
# 🔴 TUYỆT ĐỐI KHÔNG chạy ở `/decide-next`: đường đó ĐỒNG BỘ bên trong request upload câu trả lời
# và đã đo được 96s trên bài dài (trần decider là 90s). Thêm một lượt nhúng vào đó là bóp chết
# đúng chỗ đang yếu nhất.
#
# BA ĐIỀU KIỆN CHẠY (thiếu một là bỏ qua êm — xem `resolve_context`):
#   1. cờ bật · 2. job có `CampaignId` (B2B) · 3. `AttemptNo == 1`.
# (2) là van BC-6: B2C là LUYỆN TẬP, không có giám sát chống gian lận — cắm cờ vào buổi luyện là
# đo sai đối tượng. (3) vì self-consistency (E10) chấm CÙNG một answer N lần: chạy mọi attempt là
# nhân bản cả chi phí lẫn cờ.
#
# GEN-4: KHÔNG ghi DB. Cờ đi qua callback nội bộ về CampaignService (chủ sở hữu `session_flags`).
#
# ⚠ v1 KHÔNG hứa: giọng CHỒNG NHAU (hai người nói cùng lúc), giọng thì thầm/ở xa mic, và phân
# biệt >2 người (mọi thứ ngoài giọng chính bị gom làm "giọng thứ hai"). Xem báo cáo hiệu chuẩn.
import logging
import threading
from typing import NamedTuple

import aiohttp
import numpy as np

from app.config import settings

logger = logging.getLogger(__name__)

SIGNAL_TYPE = "multi_voice"
SAMPLE_RATE = 16000


class MultiVoiceContext(NamedTuple):
    """Ngữ cảnh đủ để gửi cờ về đúng buổi thi. Dựng được ⇔ đã qua cả ba điều kiện chạy."""

    answer_id: str
    session_id: str
    campaign_id: str
    candidate_id: str


class MultiVoiceResult(NamedTuple):
    detected: bool
    second_speaker_sec: float
    """Tổng thời lượng tiếng nói quy cho cụm NHỎ HƠN (giây)."""
    separation: float
    """Khoảng cách cosine giữa tâm hai cụm ∈ [0, 2]. Càng lớn = hai giọng càng khác nhau."""
    num_windows: int


def _field(body: dict, camel: str):
    """Đọc một field của job CẢ HAI kiểu viết hoa/thường.

    🔴 KHÔNG phải phòng thủ thừa: `ScoringJobPublisher.cs` gọi `JsonSerializer.Serialize(job)`
    KHÔNG truyền options ⇒ `JsonSerializerOptions.Default` ⇒ khoá trên hàng đợi là **PascalCase**,
    khác hẳn đường HTTP của ASP.NET Core (camelCase). `worker.py` vốn đã phải tra hai kiểu cho
    `transcript`/`deliveryMetrics`/`transcriptEngine`/`seniority` — cùng lý do, cùng cách. Chỉ đọc
    một kiểu thì field chết IM LẶNG: không lỗi, không cảnh báo, chỉ là một tính năng không bao giờ
    chạy (`CampaignId` luôn None ⇒ mọi job đều bị coi là B2C ⇒ detector không bao giờ bật).
    """
    v = body.get(camel)
    if v is None:
        v = body.get(camel[0].upper() + camel[1:])
    return v


def resolve_context(body: dict) -> MultiVoiceContext | None:
    """Ba điều kiện chạy → ngữ cảnh, hoặc ``None`` để bỏ qua ÊM (không phải lỗi).

    VÌ SAO LÀ HÀM RIÊNG chứ không phải ``if`` trong ``worker.process_message``: hàm đó cần
    RabbitMQ + S3 + Gemini nên không unit-test được, mà đây lại là **cổng kill-switch** của một
    tính năng thử nghiệm gắn cờ gian lận lên hồ sơ ứng viên. Guard nằm trong hàm không test được
    thì gỡ nó đi cũng **không test nào đỏ** — đúng bài học C14 (`maybe_start_cv_screening_consumer`).
    """
    if not settings.multi_voice_enabled:
        return None

    # Van B2C (BC-6). `CampaignId` chỉ được .NET điền khi `session.CampaignId != null`.
    campaign_id = _field(body, "campaignId")
    candidate_id = _field(body, "candidateId")
    if not campaign_id or not candidate_id:
        return None

    # E10 — chỉ attempt ĐẦU. Job cũ không mang `attemptNo` → mặc định 1 (giống worker.py).
    attempt_no = _field(body, "attemptNo") or 1
    try:
        if int(attempt_no) != 1:
            return None
    except (TypeError, ValueError):
        return None

    answer_id = _field(body, "answerId")
    session_id = _field(body, "sessionId")
    if not answer_id or not session_id:
        return None

    return MultiVoiceContext(
        answer_id=str(answer_id), session_id=str(session_id),
        campaign_id=str(campaign_id), candidate_id=str(candidate_id))


class SpeakerEmbedder:
    """Nhúng giọng nói bằng model ONNX (CAM++), nạp LƯỜI + khoá — mẫu `app/face_verify.py`.

    Nạp lười vì worker chấm chạy 24/7 còn detector này mặc định TẮT: nạp lúc import nghĩa là mọi
    tiến trình worker trả bộ nhớ thường trú cho một model có thể không bao giờ được gọi.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._session = None
        self._input_name: str | None = None

    @property
    def session(self):
        # Double-checked locking: worker chạy `scoring_prefetch` (10) coroutine song song và
        # detector chạy qua `asyncio.to_thread` ⇒ hai job cùng lúc sẽ dựng hai session nếu không khoá.
        if self._session is None:
            with self._lock:
                if self._session is None:
                    import onnxruntime as ort

                    logger.info("Nạp model nhúng giọng nói %s (lần đầu, nạp lười)",
                                settings.multi_voice_model_path)
                    opts = ort.SessionOptions()
                    # Worker đã chạy nhiều job song song; để onnxruntime tự mở thêm thread nội bộ
                    # là hai tầng song song chồng nhau, tranh CPU với chính lượt chấm.
                    opts.intra_op_num_threads = 1
                    opts.inter_op_num_threads = 1
                    session = ort.InferenceSession(
                        settings.multi_voice_model_path,
                        sess_options=opts, providers=["CPUExecutionProvider"])
                    self._input_name = session.get_inputs()[0].name
                    # Gán SAU khi dựng xong (mẫu face_verify): gán trước rồi ném thì job kế đọc
                    # được một session nửa vời — lỗi khác hẳn, khó lần.
                    self._session = session
        return self._session

    def embed(self, feats_batch: np.ndarray) -> np.ndarray:
        """``[N, T, 80]`` fbank → ``[N, D]`` vector nhúng đã chuẩn hoá L2."""
        out = self.session.run(None, {self._input_name: feats_batch.astype(np.float32)})[0]
        out = np.asarray(out, dtype=np.float64)
        norms = np.linalg.norm(out, axis=1, keepdims=True)
        # Vector 0 (khung câm bất thường) → giữ 0 thay vì chia cho 0 → cosine với nó = 0.
        return np.divide(out, norms, out=np.zeros_like(out), where=norms > 0)


_embedder = SpeakerEmbedder()


def plan_windows(total_speech_sec: float, win_sec: float, hop_sec: float,
                 max_windows: int) -> list[float]:
    """Mốc BẮT ĐẦU (giây, trên trục tiếng-nói-đã-nối) của các cửa sổ cần nhúng.

    Vượt trần thì **lấy mẫu ĐỀU trên toàn bộ** chứ không cắt đuôi: cắt đuôi biến "trần chi phí"
    thành "chỉ nghe phần đầu câu trả lời" ⇒ người thứ hai nói ở nửa sau **không bao giờ bị phát
    hiện**, mà triệu chứng là con số 0 hoàn toàn hợp lý — không ai nhận ra tính năng đã mù.
    """
    if total_speech_sec < win_sec:
        return []
    n = int((total_speech_sec - win_sec) // hop_sec) + 1
    starts = [i * hop_sec for i in range(n)]
    if len(starts) <= max_windows:
        return starts
    idx = np.linspace(0, len(starts) - 1, max_windows)
    return [starts[int(round(i))] for i in idx]


def _average_linkage_two_clusters(embs: np.ndarray) -> np.ndarray:
    """Gom cụm phân cấp average-linkage trên khoảng cách cosine, cắt thành ĐÚNG 2 cụm.

    Tự cài (≈25 dòng, N ≤ trần cửa sổ nên O(N³) ngây thơ vẫn rẻ) thay vì kéo `scipy.cluster`:
    scipy hiện chỉ có mặt trong image nhờ là phụ thuộc BẮC CẦU của insightface, mà repo đã chốt
    một đường chạy production không được sống nhờ phụ thuộc bắc cầu của thư viện khác (xem ghi
    chú `httpx` trong requirements.txt). Khai thêm scipy chỉ để dùng một hàm là trả giá lớn hơn.
    """
    n = embs.shape[0]
    dist = 1.0 - (embs @ embs.T)
    np.fill_diagonal(dist, 0.0)

    clusters = [[i] for i in range(n)]
    while len(clusters) > 2:
        best = None
        best_d = np.inf
        for a in range(len(clusters)):
            for b in range(a + 1, len(clusters)):
                d = dist[np.ix_(clusters[a], clusters[b])].mean()
                if d < best_d:
                    best_d, best = d, (a, b)
        a, b = best
        clusters[a] = clusters[a] + clusters[b]
        clusters.pop(b)

    labels = np.zeros(n, dtype=int)
    for lbl, members in enumerate(clusters):
        labels[members] = lbl
    return labels


def detect_from_pcm(pcm: np.ndarray, speech_spans: list[tuple[float, float]]) -> MultiVoiceResult:
    """PCM 16 kHz + các vùng CÓ TIẾNG NÓI (VAD) → kết luận có giọng thứ hai hay không.

    Nối các vùng tiếng nói thành MỘT trục liên tục rồi mới cắt cửa sổ: cắt trực tiếp trên audio
    gốc sẽ có cửa sổ rơi trọn vào khoảng lặng, và vector nhúng của sự im lặng là nhiễu thuần —
    nó tự gom thành một "cụm" và trông y hệt người thứ hai.
    """
    from app.fbank import compute_fbank

    win_sec = settings.multi_voice_window_sec
    hop_sec = settings.multi_voice_hop_sec

    pieces = [pcm[int(s * SAMPLE_RATE):int(e * SAMPLE_RATE)] for s, e in speech_spans]
    pieces = [p for p in pieces if p.size > 0]
    if not pieces:
        return MultiVoiceResult(False, 0.0, 0.0, 0)
    speech = np.concatenate(pieces)
    total_sec = speech.shape[0] / SAMPLE_RATE

    starts = plan_windows(total_sec, win_sec, hop_sec, settings.multi_voice_max_windows)
    # Cần ít nhất 2 cửa sổ mới có gì để phân cụm; và nếu tổng tiếng nói còn chưa gấp đôi ngưỡng
    # "giọng thứ hai" thì kết luận nào cũng không đứng vững.
    if len(starts) < 2:
        return MultiVoiceResult(False, 0.0, 0.0, len(starts))

    win_len = int(win_sec * SAMPLE_RATE)
    feats = [compute_fbank(speech[int(s * SAMPLE_RATE):int(s * SAMPLE_RATE) + win_len],
                           scale_to_int16=settings.multi_voice_scale_int16)
             for s in starts]
    frames = min(f.shape[0] for f in feats)
    batch = np.stack([f[:frames] for f in feats])

    # CMN — trừ trung bình theo THỜI GIAN trong từng cửa sổ (feature_normalize_type=global-mean
    # trong metadata model). Đây cũng chính là thứ khử phần lớn đặc tính KÊNH (mic, phòng, codec),
    # tức là lý do detector không coi "cùng người đổi khoảng cách mic" là người thứ hai.
    batch = batch - batch.mean(axis=1, keepdims=True)

    embs = _embedder.embed(batch)
    labels = _average_linkage_two_clusters(embs)

    counts = np.bincount(labels, minlength=2)
    minor = int(np.argmin(counts))
    # Quy THỜI LƯỢNG theo bước nhảy, không theo bề rộng cửa sổ: cửa sổ chồng nhau 50% nên cộng
    # 1,5s mỗi cửa sổ là đếm đôi. Cách này hụt tối đa (win - hop) trên toàn bộ — cố ý nghiêng về
    # phía ĐẾM THIẾU, vì sai số ở đây đi thẳng vào một cáo buộc gian lận.
    second_sec = float(counts[minor]) * hop_sec

    c0 = embs[labels == 0].mean(axis=0)
    c1 = embs[labels == 1].mean(axis=0)
    n0, n1 = np.linalg.norm(c0), np.linalg.norm(c1)
    separation = float(1.0 - float(c0 @ c1) / (n0 * n1)) if n0 > 0 and n1 > 0 else 0.0

    detected = (second_sec >= settings.multi_voice_min_second_sec
                and separation >= settings.multi_voice_separation_threshold)
    return MultiVoiceResult(detected, second_sec, separation, len(starts))


def build_note(answer_id: str, second_speaker_sec: float) -> str:
    """Ghi chú cho HR. 🔴 PHẢI TẤT ĐỊNH — đây là khoá dedup phía CampaignService.

    `SessionFlagController.RecordFlagAsync` chống trùng cờ `multi_voice` bằng bộ ba
    `(SessionId, SignalType, Note)`. Đường chấm CỐ Ý chạy lại (self-consistency E10 +
    `StuckAnswerRepublisher` đẩy job kẹt), nên cùng một sự kiện âm thanh tới đó nhiều lần; note
    đổi theo từng lượt (dấu thời gian, số lẻ float) là dedup **mất tác dụng hoàn toàn** và HR
    thấy `multi_voice: 3` cho MỘT lần nghi vấn — bằng chứng phồng lên, mà AC1 lại vừa đẩy cờ
    danh tính lên đầu danh sách nên số đếm phồng ăn thẳng vào thứ tự HR đọc.
    Làm tròn về GIÂY chính là để cùng một audio luôn cho cùng một chuỗi.
    """
    return f"answer {answer_id}: ~{int(round(second_speaker_sec))}s giọng thứ hai"


async def post_flag(ctx: MultiVoiceContext, note: str) -> None:
    """Gửi cờ về CampaignService (GEN-1: internal, KHÔNG qua gateway)."""
    base = settings.campaign_callback_base.rstrip("/")
    url = f"{base}/internal/session-flags"
    payload = {
        "campaignId": ctx.campaign_id,
        "sessionId": ctx.session_id,
        "candidateId": ctx.candidate_id,
        "signalType": SIGNAL_TYPE,
        "note": note,
    }
    headers = {"X-Internal-Token": settings.internal_token}
    async with aiohttp.ClientSession() as session:
        async with session.post(url, json=payload, headers=headers) as resp:
            if resp.status >= 300:
                text = await resp.text()
                raise RuntimeError(f"Callback session-flags fail {resp.status}: {text}")


async def maybe_report_multi_voice(body: dict, ensure_audio) -> bool:
    """Điểm vào DUY NHẤT cho worker. Trả ``True`` ⇔ đã gửi một cờ `multi_voice`.

    ``ensure_audio`` là callable BẤT ĐỒNG BỘ trả về đường dẫn file audio, và chỉ được gọi SAU khi
    qua hết cổng kiểm tra — để job B2C / attempt 2..N / cờ tắt không phải tải audio mà đường
    thích ứng vốn đã cố ý bỏ qua (worker bỏ Whisper khi job mang sẵn transcript).

    🔴 NUỐT MỌI EXCEPTION, kể cả callback hỏng. Đường chấm là ĐƯỜNG TIỀN (PAY-13: ứng viên đã trả
    1 credit cho buổi này); cờ chống gian lận là phụ phẩm. Một detector thử nghiệm hỏng KHÔNG
    được kéo cả lượt chấm chết theo — đó là đổi một bất tiện lấy một thiệt hại thật.
    """
    ctx = resolve_context(body)
    if ctx is None:
        return False

    try:
        if not settings.campaign_callback_base:
            # Chưa cấu hình đích gửi ⇒ có chạy cũng không ai nhận. Nói TO ở mức warning: cấu hình
            # thiếu mà im lặng đúng là cách `USAGE_SINK_BASE`/`PROMPT_REGISTRY_BASE` từng làm F22
            # và F21 tắt câm nhiều ngày mà không ai biết.
            logger.warning(
                "multi_voice bật nhưng CAMPAIGN_CALLBACK_BASE rỗng — bỏ qua (cờ sẽ không tới HR).")
            return False

        import asyncio

        audio_path = await ensure_audio()
        if not audio_path:
            return False

        result = await asyncio.to_thread(_detect_file, audio_path)
        logger.info(
            "multi_voice answer %s: detected=%s second=%.1fs sep=%.3f windows=%d",
            ctx.answer_id, result.detected, result.second_speaker_sec,
            result.separation, result.num_windows)
        if not result.detected:
            return False

        await post_flag(ctx, build_note(ctx.answer_id, result.second_speaker_sec))
        logger.info("Đã gửi cờ multi_voice cho session %s (answer %s)", ctx.session_id, ctx.answer_id)
        return True
    except Exception as e:
        logger.warning("Detector multi_voice lỗi cho answer %s (bỏ qua, KHÔNG chặn chấm): %s",
                       ctx.answer_id, e)
        return False


def _detect_file(audio_path: str) -> MultiVoiceResult:
    """Giải mã audio → VAD → detect. Chạy trong thread (blocking: ffmpeg + ONNX)."""
    from faster_whisper.audio import decode_audio

    from app.transcriber import VAD_OPTIONS
    from faster_whisper.vad import get_speech_timestamps

    pcm = decode_audio(audio_path, sampling_rate=SAMPLE_RATE)
    # Tái dùng ĐÚNG bộ tham số VAD của F11 (`app/transcriber.VAD_OPTIONS`) thay vì khai bộ mới:
    # hai bộ VAD lệch nhau nghĩa là chỉ số cách nói và detector giọng nhìn thấy hai bản ghi khác
    # nhau, và không có gì báo cho ai biết điều đó.
    spans = [(t["start"] / SAMPLE_RATE, t["end"] / SAMPLE_RATE)
             for t in get_speech_timestamps(pcm, VAD_OPTIONS, sampling_rate=SAMPLE_RATE)]
    return detect_from_pcm(pcm, spans)
