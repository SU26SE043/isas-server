# app/audio.py — chuyển PCM thô (Gemini TTS trả về) sang MP3.
#
# VÌ SAO CẦN BƯỚC NÀY: Gemini TTS KHÔNG trả mp3. Nó trả PCM 16-bit little-endian,
# mono, 24kHz (mime_type dạng `audio/L16;codec=pcm;rate=24000`) — nghĩa là dữ liệu
# thô KHÔNG có header, trình duyệt không phát trực tiếp được. Hợp đồng API với FE
# đã chốt `audio/mpeg`, nên phải encode lại.
#
# DÙNG ffmpeg (subprocess) thay vì thêm thư viện encode mới: ffmpeg ĐÃ có sẵn trong
# image (Dockerfile cài cho faster-whisper decode webm/opus) và bản Debian được build
# kèm libmp3lame → không phát sinh dependency mới, không phải kiểm thêm wheel/arch.
import subprocess

# Mặc định khớp Gemini TTS (24kHz mono s16le). Vẫn parse lại từ mime_type khi có
# (xem parse_pcm_rate) phòng model đổi sample-rate ở bản sau.
DEFAULT_SAMPLE_RATE = 24000
DEFAULT_CHANNELS = 1

# Bitrate mp3: giọng nói 1 kênh 24kHz → 64kbps là dư sức trong (~8KB/giây audio).
# Nhỏ = tải nhanh + tốn ít dung lượng S3 cache.
_MP3_BITRATE = "64k"


def parse_pcm_rate(mime_type: str | None) -> int:
    """Rút sample-rate từ mime_type kiểu `audio/L16;codec=pcm;rate=24000`.

    Không parse được → DEFAULT_SAMPLE_RATE. Sai sample-rate không làm hỏng file,
    chỉ khiến giọng nhanh/chậm bất thường — nên fallback im lặng là chấp nhận được."""
    if not mime_type:
        return DEFAULT_SAMPLE_RATE
    for part in mime_type.split(";"):
        part = part.strip()
        if part.startswith("rate="):
            try:
                rate = int(part[len("rate="):])
                return rate if rate > 0 else DEFAULT_SAMPLE_RATE
            except ValueError:
                return DEFAULT_SAMPLE_RATE
    return DEFAULT_SAMPLE_RATE


def pcm_to_mp3(pcm: bytes,
               sample_rate: int = DEFAULT_SAMPLE_RATE,
               channels: int = DEFAULT_CHANNELS) -> bytes:
    """PCM s16le thô → bytes MP3. Lỗi encode → RuntimeError (caller map 502).

    Đọc/ghi qua pipe (không temp-file): audio 1 câu hỏi chỉ vài trăm KB, giữ trong
    RAM rẻ hơn và không để lại rác khi process chết giữa chừng."""
    if not pcm:
        raise RuntimeError("PCM rỗng — không có gì để encode.")

    try:
        proc = subprocess.run(
            [
                "ffmpeg", "-hide_banner", "-loglevel", "error",
                # Input: PCM thô nên PHẢI khai format/rate/channels (không có header để ffmpeg tự dò).
                "-f", "s16le", "-ar", str(sample_rate), "-ac", str(channels), "-i", "pipe:0",
                "-codec:a", "libmp3lame", "-b:a", _MP3_BITRATE,
                "-f", "mp3", "pipe:1",
            ],
            input=pcm,
            capture_output=True,
            check=False,
        )
    except FileNotFoundError as ex:  # ffmpeg không có trong PATH (môi trường dev thiếu)
        raise RuntimeError("Không tìm thấy ffmpeg để encode mp3.") from ex

    if proc.returncode != 0:
        err = (proc.stderr or b"").decode("utf-8", "replace")[:200]
        raise RuntimeError(f"ffmpeg encode mp3 lỗi ({proc.returncode}): {err}")

    if not proc.stdout:
        raise RuntimeError("ffmpeg trả về mp3 rỗng.")

    return proc.stdout
