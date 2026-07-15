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

# Stub insightface trước khi import app.main — FaceVerifier nạp model nặng (insightface
# FaceAnalysis) + cần onnxruntime/opencv (wheel không có cho mọi Python/arch — chạy ở
# deploy trong image 3.12, xem Dockerfile). Test /face-verify chỉ cần app.main import được
# + monkeypatch FaceVerifier.compare/storage.get_object_bytes; KHÔNG chạy detect/embed thật
# → thay bằng stub no-op (giống faster_whisper ở trên). cv2/numpy import LAZY trong
# face_verify.py nên không cần stub — chỉ FaceAnalysis (nạp lúc __init__) mới cần.
if "insightface" not in sys.modules:
    fake_insightface = types.ModuleType("insightface")
    fake_insightface_app = types.ModuleType("insightface.app")

    class _FakeFaceAnalysis:
        def __init__(self, *args, **kwargs) -> None:
            pass

        def prepare(self, *args, **kwargs) -> None:
            pass

        def get(self, *args, **kwargs):
            return []

    fake_insightface_app.FaceAnalysis = _FakeFaceAnalysis
    fake_insightface.app = fake_insightface_app
    sys.modules["insightface"] = fake_insightface
    sys.modules["insightface.app"] = fake_insightface_app

# GeminiProvider() khởi tạo genai.Client(api_key=...) khi import app.main —
# cần biến môi trường để Settings() (pydantic-settings, required field) không
# lỗi ngay lúc import. Giá trị giả — test không gọi Gemini thật (luôn mock).
import os

os.environ.setdefault("GEMINI_API_KEY", "test-dummy-key")
