# app/face_verify.py — SEC-2/3: đối chiếu khuôn mặt (face verify) + đếm số mặt.
#
# insightface FaceAnalysis nạp 1 LẦN trong __init__ (như Transcriber) — model detect+embed
# nặng, load lại mỗi request rất chậm. CPU-only (onnxruntime CPUExecutionProvider).
from insightface.app import FaceAnalysis

from app.config import settings


class FaceVerifier:
    def __init__(self) -> None:
        # Model load 1 lần (dải buffalo_l: SCRFD detect + ArcFace embed).
        self._model = FaceAnalysis(
            name=settings.face_model_name,
            providers=["CPUExecutionProvider"],
        )
        # ctx_id=-1 = CPU; det_size cố định cho ổn định.
        self._model.prepare(ctx_id=-1, det_size=(640, 640))

    def _decode(self, img_bytes: bytes):
        """bytes ảnh → ndarray BGR (cv2) để insightface detect.

        cv2/numpy import LAZY (chỉ khi thực sự decode) — test stub insightface + monkeypatch
        compare nên KHÔNG cần cài opencv/numpy để chạy pytest."""
        import cv2
        import numpy as np

        arr = np.frombuffer(img_bytes, dtype=np.uint8)
        img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
        if img is None:
            raise ValueError("Không decode được ảnh (định dạng không hợp lệ?).")
        return img

    def _detect(self, img_bytes: bytes) -> list:
        """Danh sách khuôn mặt phát hiện trong ảnh (mỗi phần tử có normed_embedding)."""
        return self._model.get(self._decode(img_bytes)) or []

    def count_faces(self, img_bytes: bytes) -> int:
        """Số khuôn mặt phát hiện trong ảnh."""
        return len(self._detect(img_bytes))

    def embed(self, img_bytes: bytes):
        """Vector nhúng (ArcFace, đã chuẩn hoá) của khuôn mặt DUY NHẤT trong ảnh.

        Ảnh phải có đúng 1 mặt (gọi khi count_faces == 1)."""
        faces = self._detect(img_bytes)
        if len(faces) != 1:
            raise ValueError(f"Cần đúng 1 khuôn mặt để nhúng, có {len(faces)}.")
        return faces[0].normed_embedding

    def compare(self, ref_bytes: bytes, live_bytes: bytes) -> tuple[float, int]:
        """Đối chiếu ảnh tham chiếu ↔ ảnh live.

        Detect trên ảnh LIVE để đếm mặt (chống thi hộ / nhiều người / vắng mặt):
          - live có 0 hoặc >1 mặt → không so khớp được → score=0.0, trả kèm face_count.
          - live có đúng 1 mặt → nhúng cả 2 ảnh → cosine similarity ∈ [-1,1] → score.

        Trả về (score, face_count) với face_count = số mặt trên ảnh LIVE.
        """
        live_faces = self._detect(live_bytes)
        face_count = len(live_faces)
        if face_count != 1:
            return 0.0, face_count

        live_emb = live_faces[0].normed_embedding
        ref_emb = self.embed(ref_bytes)  # ref cũng cần đúng 1 mặt để nhúng
        return self._cosine(ref_emb, live_emb), face_count

    @staticmethod
    def _cosine(a, b) -> float:
        import numpy as np

        a = np.asarray(a, dtype=np.float32)
        b = np.asarray(b, dtype=np.float32)
        denom = float(np.linalg.norm(a) * np.linalg.norm(b))
        if denom == 0.0:
            return 0.0
        return float(np.dot(a, b) / denom)
