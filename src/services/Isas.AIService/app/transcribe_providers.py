"""Chép lời qua nhà cung cấp TỪ XA — Whisper cục bộ giữ vai DỰ PHÒNG.

VÌ SAO — Whisper `small` (bản đang chạy prod) chép sai tới mức ĐỔI NGHĨA
──────────────────────────────────────────────────────────────────────
Không phải sai chính tả vặt: "người dùng **cần** thiết" → "người dùng **tầng** thiết";
"Business Analyst" → "BGN Analyze"; "RESTful API" → "RESTfulAVI". Bản chép đó đi THẲNG
vào bộ chấm — tức ứng viên bị chấm trên những câu họ không hề nói.

Đo trên 7 ghi âm THẬT (lấy từ S3) + 3 file tổng hợp có văn bản gốc:

    engine      lỗi từ   thuật ngữ đúng   vòng lặp   thời gian/190s
    small        4,2%          5             0           39,2s
    large-v3     0,5%          7             0          175,3s
    whisper-1    0,7%          8             0           23,9s
    gemini       0,5%          9             1           29,9s

`large-v3` chép tốt nhưng chậm gấp ~4,5 lần `small` — mà `/decide-next` chạy Whisper ĐỒNG BỘ
trong request upload câu trả lời (timeout decider 90s), nên nó KHÔNG dùng được ở đường nóng.
Hai nhà cung cấp từ xa cho chất lượng ngang `large-v3` với thời gian còn dưới cả `small`.

Ô "vòng lặp" của gemini là lý do có :func:`looks_broken` — xem docstring hàm đó.

MẶC ĐỊNH VẪN LÀ `local`
───────────────────────
Đây là năng lực MỚI, có chi phí tiền và có hệ quả riêng tư (audio của ứng viên rời khỏi
hạ tầng của mình) ⇒ theo đúng tiền lệ mọi rollout khác của repo (`GROUNDING_ENABLED`,
`TIERING_ENABLED`, `CV_SCREENING_ENABLED` đều false). Xem `config.transcribe_provider`.

VÌ SAO GỬI WAV DỰNG LẠI TỪ `pcm`, KHÔNG GỬI BYTE GỐC
────────────────────────────────────────────────────
1. **59/77 file audio trong S3 mang đuôi `.webm` nhưng ruột là WAV** (dấu vết e2e cũ), mà
   OpenAI đoán định dạng theo PHẦN MỞ RỘNG ⇒ gửi thẳng byte gốc là mời một lớp lỗi chỉ nổ
   trên một phần dữ liệu và rất khó tái hiện.
2. Giữ đúng tính chất "giải mã MỘT lần" của `transcriber.py`: nhà cung cấp và VAD nhìn
   **cùng một tín hiệu**, nên chênh lệch (nếu có) không thể đến từ khâu giải mã.
Chi phí đã đo: file 45s → 1,44 MB → trọn vòng 2,8s.
"""

from __future__ import annotations

import array
import io
import logging
import re
import unicodedata
import wave
from collections import Counter

from app.config import settings

logger = logging.getLogger(__name__)

SAMPLE_RATE = 16000

LOCAL = "local"
OPENAI = "whisper-1"
GEMINI = "gemini"
REMOTE_PROVIDERS = (OPENAI, GEMINI)

OPENAI_URL = "https://api.openai.com/v1/audio/transcriptions"

# 🔴 GIỮ NGUYÊN CHUỖI NÀY — đã kiểm nghiệm trên 7 ghi âm thật.
#
# Gemini là mô hình NGÔN NGỮ nên xu hướng tự nhiên của nó là viết lại cho mượt. Ở bài toán
# này "mượt" là HẠI: bản đã làm mượt GIẤU MẤT chính thứ đang chấm — câu bỏ lửng, lặp từ,
# tự sửa lời. Ba câu cấm bên dưới là phần kéo nó về đúng vai người CHÉP, không phải người
# BIÊN TẬP; bỏ bớt câu nào cũng làm nó bắt đầu biên tập lại.
GEMINI_TRANSCRIBE_PROMPT = (
    "Chép lại NGUYÊN VĂN lời nói trong đoạn ghi âm tiếng Việt này. "
    "Giữ đúng từng từ ứng viên nói, KỂ CẢ từ đệm (ừm, ờ, à), câu bỏ lửng, lặp từ và tự sửa lời. "
    "TUYỆT ĐỐI KHÔNG sửa ngữ pháp, KHÔNG viết lại cho mượt, KHÔNG tóm tắt, KHÔNG thêm bình luận. "
    "Chỉ trả về phần lời nói, không gì khác."
)

# Vết bẩn dữ liệu HUẤN LUYỆN của Whisper: nó được học trên phụ đề YouTube nên khi tín hiệu
# yếu/mồi sai, nó "chép" ra câu kết video thay vì lời người nói. Đây KHÔNG phải chép sai —
# đây là MẤT TRẮNG bài làm mà ứng viên đã trả credit để làm.
JUNK_MARKERS = (
    "subscribe",
    "ghiền mì gõ",
    "video hấp dẫn",
    "đăng ký kênh",
    "hãy like",
    "cảm ơn các bạn đã theo dõi",
    "phụ đề",
)

# Độ dài (số từ) tối thiểu của cụm được xét khi dò vòng lặp.
#
# 🔴 LUẬT NÀY ĐƯỢC CHỐT BẰNG SỐ ĐO, KHÔNG PHẢI BẰNG CẢM TÍNH — đọc trước khi chỉnh.
#
# Lá chắn này bắt oan được ĐÚNG nhóm người dùng của mình: ứng viên hồi hộp LẶP LỜI THẬT.
# Chạy bộ dò trên 4 engine × 7 ghi âm THẬT (28 bản chép) + 3 mẫu hỏng đã quan sát được:
#
#   luật                                  dương tính giả /28    bắt được mẫu hỏng
#   "cụm 6 từ xuất hiện ≥2 lần"                   1                   2/3
#   "cụm 6 từ xuất hiện ≥3 lần"                   0                   1/3
#   "khối ≥6 từ lặp NGAY SAU chính nó"            0                   3/3
#   ghép hai luật cuối  ← CHỌN                    0                   3/3
#
# Dương tính giả duy nhất là bản gemini của ghi âm r2 — ứng viên nói thật hai lần "hiểu được
# luồng đi của người dùng", cách nhau 12 từ. Bắt nhầm ca đó KHÔNG vô hại: nó vứt bản chép TỐT
# NHẤT (lỗi từ 0,5%) để dùng bản cục bộ (4,2%) — tức lá chắn làm chất lượng TỆ ĐI đúng trên
# những câu trả lời ngập ngừng, là nhóm cần chép chính xác nhất.
#
# Phân biệt đúng nằm ở chỗ: người lặp lời thì giữa hai lần có nội dung khác chen vào; decoder
# kẹt thì phun lại khối cũ NGAY LẬP TỨC. Nên luật là "khối lặp KỀ NHAU", cộng vế "≥3 lần" cho
# vòng lặp có trôi nhẹ giữa các vòng (dấu câu/từ đệm khác nhau làm mất tính kề).
LOOP_NGRAM_WORDS = 6
# Số lần một cụm phải xuất hiện mới coi là vòng lặp (khi các lần lặp KHÔNG kề nhau).
LOOP_MIN_OCCURRENCES = 3

_PUNCT = re.compile(r"""[.,!?;:…"'“”‘’()\[\]–—-]+""")
_SPACES = re.compile(r"\s+")


def _normalize_words(text: str) -> list[str]:
    """Chuẩn hoá để so khớp: NFC + thường + bỏ dấu câu + gộp khoảng trắng → danh sách từ.

    NFC chứ không bỏ dấu tiếng Việt: các chuỗi rác đã biết đều CÓ dấu ("ghiền mì gõ"), bỏ
    dấu sẽ làm chúng khớp cả những câu vô tội khác.
    """
    lowered = unicodedata.normalize("NFC", text or "").lower()
    return _SPACES.sub(" ", _PUNCT.sub(" ", lowered)).strip().split()


def _repeated_block(words: list[str]) -> str | None:
    """Có KHỐI ≥``LOOP_NGRAM_WORDS`` từ lặp lại NGAY SAU chính nó không? (chữ ký decoder kẹt)

    Chu kỳ ``p`` là BẤT KỲ, không phải bằng đúng ``LOOP_NGRAM_WORDS`` — một phiên bản chỉ kiểm
    ``p == n`` sẽ bỏ lọt mẫu vòng lặp thật (chu kỳ 10-15 từ) trong khi trông vẫn rất hợp lý.

    Chỉ xét các chu kỳ ỨNG VIÊN: nếu một khối lặp với chu kỳ ``p`` thì cụm ``n`` từ ở vị trí
    ``a`` cũng phải xuất hiện lại ở ``a+p``. Quét mọi ``p`` một cách ngây thơ là O(n³) — với câu
    trả lời dài vài trăm từ nó đủ chậm để thành vấn đề trên đường ĐỒNG BỘ của `/decide-next`.
    """
    positions: dict[tuple[str, ...], list[int]] = {}
    for i in range(len(words) - LOOP_NGRAM_WORDS + 1):
        positions.setdefault(tuple(words[i:i + LOOP_NGRAM_WORDS]), []).append(i)

    for occurrences in positions.values():
        for a, b in zip(occurrences, occurrences[1:]):
            period = b - a
            if period >= LOOP_NGRAM_WORDS and words[a:b] == words[b:b + period]:
                return " ".join(words[a:b])
    return None


def looks_broken(text: str) -> str | None:
    """Bản chép có dấu hiệu HỎNG không? Trả LÝ DO (chuỗi) hoặc None. Hàm THUẦN.

    Ba dấu hiệu, đều rút từ hỏng hóc QUAN SÁT ĐƯỢC chứ không phải phòng xa:

    1. **Chuỗi rác phụ đề YouTube** — xem :data:`JUNK_MARKERS`. Nguy hiểm nhất, nên xét trước.
    2. **Khối lặp kề nhau** — chữ ký của decoder kẹt (:func:`_repeated_block`).
    3. **Một cụm 6 từ xuất hiện ≥3 lần** — vòng lặp có trôi nhẹ giữa các vòng.

    Ngưỡng của (2)+(3) đã được đo để KHÔNG bắt oan người nói lặp thật — xem
    :data:`LOOP_NGRAM_WORDS`.

    🔴 VÌ SAO PHẢI CÓ HÀM NÀY — VÀ VÌ SAO TUYỆT ĐỐI KHÔNG MỒI TỪ VỰNG:
    đã thử truyền danh sách thuật ngữ vào ``prompt``/``initial_prompt`` để cứu thuật ngữ. Trên
    một file, mồi làm **TOÀN BỘ câu trả lời của ứng viên bị thay bằng**
    "Hãy subscribe cho kênh Ghiền Mì Gõ Để không bỏ lỡ những video hấp dẫn." ×2.
    Nguy hiểm nhất là **mọi chỉ số gộp lúc đó đều ĐẸP** (thuật ngữ đúng 5→8, ký tự giảm 13%)
    — vì cả bài bị thay bằng một vòng lặp ngắn. Nếu chỉ nhìn bảng số thì đó trông như một
    cải tiến. Xem thêm cảnh báo ở :func:`transcribe_openai`.
    """
    words = _normalize_words(text)
    joined = " ".join(words)

    for marker in JUNK_MARKERS:
        if marker in joined:
            return f"chứa chuỗi rác đã biết: {marker!r}"

    block = _repeated_block(words)
    if block is not None:
        return f"lặp lại ngay khối {len(block.split())} từ: {block!r}"

    counts = Counter(
        " ".join(words[i:i + LOOP_NGRAM_WORDS])
        for i in range(len(words) - LOOP_NGRAM_WORDS + 1)
    )
    for gram, times in counts.items():
        if times >= LOOP_MIN_OCCURRENCES:
            return f"cụm {LOOP_NGRAM_WORDS} từ xuất hiện {times} lần: {gram!r}"

    return None


def pcm_to_wav_bytes(pcm, sample_rate: int = SAMPLE_RATE) -> bytes:
    """Mảng float32 [-1,1] (đầu ra `decode_audio`) → WAV 16-bit mono trong bộ nhớ.

    Không ghi ra đĩa: file tạm cho một lượt gọi mạng là thêm một đường rò (và một chỗ phải
    dọn) mà không đổi lại được gì.

    Có nhánh numpy cho đường chạy thật (720k mẫu cho 45s audio) và nhánh thuần Python cho
    danh sách — stub `decode_audio` trong conftest trả `list`, và một hàm chuyển đổi mà chỉ
    chạy được với đúng một kiểu dữ liệu là hàm khó test.
    """
    try:
        import numpy as np

        if isinstance(pcm, np.ndarray):
            clipped = np.clip(np.asarray(pcm, dtype="float32"), -1.0, 1.0)
            samples = (clipped * 32767.0).astype("<i2").tobytes()
        else:
            raise TypeError
    except (ImportError, TypeError):
        ints = array.array(
            "h", (int(max(-1.0, min(1.0, float(s))) * 32767) for s in pcm))
        samples = ints.tobytes()

    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(samples)
    return buf.getvalue()


# Content-Type theo ĐUÔI FILE, tra bảng tường minh — CỐ Ý KHÔNG dùng `mimetypes.guess_type`.
# `mimetypes` đọc /etc/mime.types nên kết quả phụ thuộc image/OS, và trên image `python:3.12-slim`
# nó cho ra những giá trị SAI cho đúng đường đang chạy thật:
#     .webm → video/webm   (đuôi DUY NHẤT xuất hiện trên production — AnswerService.cs:80)
#     .ogg .oga .m4a .mpga .flac → None → application/octet-stream
#     .wav  → audio/x-wav
# Gửi sai Content-Type thì nhà cung cấp có thể từ chối; lúc đó nhánh cứu WAV nuốt lỗi ⇒ tính năng
# gửi-file-gốc KHÔNG BAO GIỜ kích hoạt mà không ai biết (mẫu "tắt câm" đã dính 3 lần: env F21/F22,
# consumer C14). Bảng tĩnh làm hành vi giống nhau ở mọi máy và đọc được bằng mắt.
AUDIO_CONTENT_TYPES = {
    ".webm": "audio/webm", ".ogg": "audio/ogg", ".oga": "audio/ogg",
    ".mp3": "audio/mpeg", ".mpga": "audio/mpeg", ".mpeg": "audio/mpeg",
    ".m4a": "audio/mp4", ".mp4": "audio/mp4",
    ".flac": "audio/flac", ".wav": "audio/wav",
}


def audio_content_type(filename: str) -> str:
    """Content-Type cho payload chép lời. Đuôi lạ → octet-stream (nhà cung cấp tự dò theo tên file)."""
    dot = filename.rfind(".")
    ext = filename[dot:].lower() if dot != -1 else ""
    return AUDIO_CONTENT_TYPES.get(ext, "application/octet-stream")


def transcribe_openai(audio_bytes: bytes, language: str | None,
                      audio_seconds: float = 0.0, filename: str = "audio.wav") -> tuple[str, str]:
    """`whisper-1` của OpenAI. Trả ``(text, engine)``. RAISE khi hỏng (caller lo dự phòng).

    🔴 KHÔNG BAO GIỜ THÊM KHOÁ ``prompt`` VÀO PAYLOAD NÀY.
    ``prompt`` là chỗ mồi từ vựng cho model. Đã thử — và trên một file thật nó làm **toàn bộ
    câu trả lời của ứng viên bị thay bằng** một câu kết video YouTube lặp 2 lần (vết bẩn dữ
    liệu huấn luyện). Đó là mất trắng bài làm đã tốn 1 credit, chứ không phải chép sai vài từ.
    Có test khoá lại điều này (``test_transcribe_providers.py``); nếu ai đó lại định "cải tiến
    chất lượng thuật ngữ" bằng cách mồi từ vựng thì đọc :func:`looks_broken` trước.
    """
    import httpx

    from app.usage import report_audio_usage, report_blocking

    files = {"file": (filename, audio_bytes, audio_content_type(filename))}
    data = {
        "model": settings.openai_transcribe_model,
        "response_format": "json",
    }
    # `language` rỗng/None → để nhà cung cấp tự dò, đừng gửi khoá rỗng (API từ chối "").
    if language and language.strip():
        data["language"] = language.strip()

    headers = {"Authorization": f"Bearer {settings.openai_api_key}"}
    with httpx.Client(timeout=settings.transcribe_timeout_seconds) as client:
        resp = client.post(OPENAI_URL, headers=headers, data=data, files=files)
        resp.raise_for_status()
        text = (resp.json().get("text") or "").strip()

    # whisper-1 tính tiền theo PHÚT (không phải token) ⇒ số đo phải là GIÂY AUDIO. Best-effort,
    # xem `usage.report_blocking`. HTTP 200 vẫn là một lượt đã bị tính tiền, kể cả khi payload
    # rỗng và caller thử lại định dạng WAV.
    report_blocking(report_audio_usage(
        "transcribe", settings.openai_transcribe_model, audio_seconds))
    if not text:
        raise ValueError("whisper-1 trả bản chép rỗng")
    return text, settings.openai_transcribe_model


_gemini_client = None


def _get_gemini_client():
    """Client dựng LƯỜI + dùng lại: dựng mỗi lượt gọi là thêm một lần bắt tay TLS vào đúng
    đường đồng bộ của `/decide-next`."""
    global _gemini_client
    if _gemini_client is None:
        from google import genai

        _gemini_client = genai.Client(api_key=settings.gemini_api_key)
    return _gemini_client


def transcribe_gemini(audio_bytes: bytes, language: str | None,
                      audio_seconds: float = 0.0, filename: str = "audio.wav") -> tuple[str, str]:
    """Gemini đa phương thức. Trả ``(text, engine)``. RAISE khi hỏng (caller lo dự phòng).

    Dùng API ĐỒNG BỘ (``client.models``) chứ không ``client.aio.models`` như
    `providers/gemini.py`: hàm này chạy trong thread do ``asyncio.to_thread`` cấp (xem
    `transcriber.py`), nơi không có event loop nào để ``await``.

    Cũng vì thế nó KHÔNG đi qua chokepoint ``GeminiProvider._generate`` — nhưng vẫn phải ghi
    nhận token, nên gọi thẳng ``report_usage`` (F22). Gemini tính theo TOKEN và trả sẵn
    ``usage_metadata`` ⇒ không cần gì thêm (đo được ~36 token/giây audio).
    """
    from google.genai import types

    from app.usage import report_blocking, report_usage

    resp = _get_gemini_client().models.generate_content(
        model=settings.gemini_model,
        contents=[
            types.Part.from_bytes(data=audio_bytes, mime_type=audio_content_type(filename)),
            GEMINI_TRANSCRIBE_PROMPT,
        ],
        config=types.GenerateContentConfig(temperature=0.0),
    )
    report_blocking(report_usage("transcribe", settings.gemini_model, resp))

    text = (getattr(resp, "text", None) or "").strip()
    if not text:
        raise ValueError("Gemini trả bản chép rỗng")
    return text, settings.gemini_model


def transcribe_remote(provider: str, audio_bytes: bytes, language: str | None,
                      audio_seconds: float = 0.0, filename: str = "audio.wav") -> tuple[str, str]:
    """Điều phối theo tên nhà cung cấp. RAISE khi tên lạ hoặc lượt gọi hỏng."""
    if provider == OPENAI:
        return transcribe_openai(audio_bytes, language, audio_seconds, filename)
    if provider == GEMINI:
        return transcribe_gemini(audio_bytes, language, audio_seconds, filename)
    raise ValueError(f"transcribe_provider không nhận ra: {provider!r}")
