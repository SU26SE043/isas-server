# app/tts.py — quy ước CACHE KEY cho audio đọc câu hỏi.
#
# Key ĐỊNH-DANH-THEO-NỘI-DUNG: tts/{sha256(voice + text)}.mp3
#
# Vì sao không key theo questionId:
#   • Câu hỏi TRÙNG NHAU dùng chung 1 file. Đặc biệt B2B: seed câu hỏi của campaign phát
#     cho MỌI ứng viên — key theo questionId sẽ tổng hợp (và trả tiền) lại cho từng người,
#     key theo nội dung thì tổng hợp đúng 1 lần cho cả chiến dịch.
#   • Sửa nội dung câu hỏi ⇒ hash đổi ⇒ tự vô hiệu hoá cache, không cần purge tay và không
#     bao giờ đọc nhầm audio cũ của câu đã sửa.
#   • KHÔNG cần bảng/cột nào ⇒ KHÔNG cần migration.
#
# voice nằm trong hash vì đổi giọng phải ra file khác. language_code KHÔNG nằm trong hash:
# nó là hằng phía server (settings.tts_language_code), client không truyền. NẾU sau này cho
# phép chọn ngôn ngữ per-request thì PHẢI thêm nó vào đây, nếu không hai ngôn ngữ khác nhau
# của cùng một câu sẽ đụng chung 1 key và trả nhầm audio.
import hashlib

from app.config import settings

# Hợp đồng với FE đã chốt audio/mpeg.
MP3_CONTENT_TYPE = "audio/mpeg"


def cache_key(text: str, voice: str) -> str:
    """(text, voice) → key S3 ổn định. Cùng input ⇒ cùng key (giữa các process/lần deploy)."""
    # "\x00" ngăn nhập nhằng ghép chuỗi: (voice="A", text="BC") và (voice="AB", text="C")
    # phải ra 2 key khác nhau.
    digest = hashlib.sha256(f"{voice}\x00{text}".encode("utf-8")).hexdigest()
    return f"{settings.tts_cache_prefix}{digest}.mp3"
