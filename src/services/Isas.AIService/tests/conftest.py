# tests/conftest.py
#
# Stub `faster_whisper` trước khi import app.main: WhisperModel thật cần
# ctranslate2 (compiled wheel không có cho mọi Python — vd 3.13/3.14, xem
# comment trong Dockerfile) và tải model nặng khi khởi tạo. Test này chỉ
# cần app.main import được (để lấy `app`/route `/analyze-cv`), KHÔNG cần
# transcribe thật — nên thay bằng stub no-op, không đụng logic đang test.
import sys
import types

if "faster_whisper" not in sys.modules:
    fake_module = types.ModuleType("faster_whisper")

    class _FakeWhisperModel:
        def __init__(self, *args, **kwargs) -> None:
            pass

        def transcribe(self, *args, **kwargs):
            return [], None

    fake_module.WhisperModel = _FakeWhisperModel
    sys.modules["faster_whisper"] = fake_module

# GeminiProvider() khởi tạo genai.Client(api_key=...) khi import app.main —
# cần biến môi trường để Settings() (pydantic-settings, required field) không
# lỗi ngay lúc import. Giá trị giả — test không gọi Gemini thật (luôn mock).
import os

os.environ.setdefault("GEMINI_API_KEY", "test-dummy-key")
