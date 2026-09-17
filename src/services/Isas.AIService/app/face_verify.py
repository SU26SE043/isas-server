# app/face_verify.py — SEC-2/3: đối chiếu khuôn mặt (face verify) + đếm số mặt.
#
# insightface FaceAnalysis nạp LƯỜI — dựng ở lần dùng đầu, không phải lúc import (như Transcriber).
# Model detect+embed nặng, vẫn chỉ nạp 1 lần rồi dùng lại. CPU-only (onnxruntime CPUExecutionProvider).
import hashlib
import logging
import threading
from collections import OrderedDict
from typing import NamedTuple

from insightface.app import FaceAnalysis

from app.config import settings

logger = logging.getLogger(__name__)


class FaceCompareResult(NamedTuple):
    """Kết quả đối chiếu — tách rõ "ảnh MỐC hỏng" khỏi "người KHÁC".

    Trước đây `compare` chỉ trả `(score, face_count)`, nuốt mất việc ảnh mốc có đọc được mặt
    hay không ⇒ enroll hỏng (ảnh đen do webcam chưa phơi sáng) cho ra score 0.0, và caller
    kết luận `face_mismatch` — tức ĐỔ LỖI cho ứng viên trung thực, mỗi 30s suốt buổi thi.
    Hai ca đó dẫn tới hai quyết định khác hẳn nhau của HR nên phải phân biệt được ở đây.
    """

    score: float
    face_count: int
    """Số mặt trên ảnh LIVE."""
    reference_face_count: int | None
    """Số mặt trên ảnh MỐC. `None` = CHƯA XÉT (live đã không so được nên không cần detect ảnh
    mốc) — cố ý không dùng 0, vì 0 nghĩa là "đã nhìn và không thấy mặt nào"."""


class FaceVerifier:
    def __init__(self) -> None:
        # Đo trong image (`ru_maxrss`): +`FaceAnalysis(buffalo_l)` = **358 MB**. Chỉ `main.py` dựng
        # FaceVerifier (worker không import file này), nhưng face-verify là đường HIẾM — nạp lúc
        # import nghĩa là mọi tiến trình api trả 358 MB thường trú cho một tính năng có thể cả
        # ngày không ai gọi.
        self._model_lock = threading.Lock()
        self._model_instance: FaceAnalysis | None = None
        # Cache vector nhúng của ẢNH MỐC, khoá = sha256(nội dung ảnh) — xem `_reference_faces`.
        self._ref_cache: OrderedDict[str, tuple[int, object]] = OrderedDict()
        self._ref_cache_lock = threading.Lock()

    # Ảnh mốc của một ứng viên không đổi suốt buổi thi, nhưng trước bản này nó bị detect + nhúng
    # lại ở MỌI lượt kiểm mặt (30s/lần). Đo trên box 8 core (2026-09-17): detect ~96ms/ảnh,
    # nhúng ArcFace ~270ms/ảnh ⇒ `compare(ref, live)` ~730ms, trong đó ~370ms là làm lại việc
    # đã làm cho ảnh mốc. Nhớ kết quả theo NỘI DUNG ảnh (không theo S3 key): enroll lại ghi đè
    # cùng key `face-reference.jpg`, khoá theo key thì ảnh mốc mới vẫn ăn vector của ảnh cũ —
    # đúng ca "mốc đen do webcam chưa phơi sáng" mà bước enroll-lại sinh ra để cứu. Băm 19KB
    # tốn micro-giây, nhỏ hơn hẳn 24ms tải S3 vốn vẫn phải làm.
    _REF_CACHE_MAX = 256

    def _reference_faces(self, ref_bytes: bytes) -> tuple[int, object]:
        """(số mặt trên ảnh mốc, vector nhúng nếu đúng 1 mặt else None) — có cache theo nội dung.

        Cache cả ca mốc hỏng (0 / nhiều mặt): kết luận đó cũng chỉ phụ thuộc nội dung ảnh, và
        mốc hỏng bị hỏi lại mỗi 10s (nhịp báo động) nên càng đáng nhớ. Không cache khi detect ném
        (lỗi tạm thời không phải thuộc tính của ảnh)."""
        key = hashlib.sha256(ref_bytes).hexdigest()
        with self._ref_cache_lock:
            hit = self._ref_cache.get(key)
            if hit is not None:
                self._ref_cache.move_to_end(key)
                return hit
        faces = self._detect(ref_bytes)
        entry = (len(faces), faces[0].normed_embedding if len(faces) == 1 else None)
        with self._ref_cache_lock:
            self._ref_cache[key] = entry
            self._ref_cache.move_to_end(key)
            while len(self._ref_cache) > self._REF_CACHE_MAX:
                self._ref_cache.popitem(last=False)
        return entry

    @property
    def _model(self) -> FaceAnalysis:
        # Double-checked locking — `/face-verify` chạy qua `asyncio.to_thread` (main.py:222) nên
        # hai request đồng thời sẽ dựng hai model nếu không khoá.
        if self._model_instance is None:
            with self._model_lock:
                if self._model_instance is None:
                    logger.info("Nạp FaceAnalysis %s (lần đầu, nạp lười)", settings.face_model_name)
                    model = FaceAnalysis(
                        name=settings.face_model_name,
                        providers=["CPUExecutionProvider"],
                    )
                    # ctx_id=-1 = CPU; det_size cố định cho ổn định.
                    model.prepare(ctx_id=-1, det_size=(640, 640))
                    # Gán SAU khi prepare xong: gán trước rồi prepare mà ném thì request kế đọc
                    # được một model chưa prepare (lỗi khác hẳn, khó lần).
                    self._model_instance = model
        return self._model_instance

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

    def compare(self, ref_bytes: bytes, live_bytes: bytes) -> FaceCompareResult:
        """Đối chiếu ảnh tham chiếu ↔ ảnh live.

        Detect trên ảnh LIVE để đếm mặt (chống thi hộ / nhiều người / vắng mặt):
          - live có 0 hoặc >1 mặt → không so khớp được → score=0.0, trả kèm face_count.
          - live có đúng 1 mặt → nhúng cả 2 ảnh → cosine similarity ∈ [-1,1] → score.

        Xem FaceCompareResult về ý nghĩa từng trường.
        """
        live_faces = self._detect(live_bytes)
        face_count = len(live_faces)
        if face_count != 1:
            # Live đã không so được → khỏi detect ảnh mốc (tiết kiệm một lượt model).
            # reference_face_count = None vì ta CHƯA NHÌN, không phải "nhìn rồi không thấy".
            return FaceCompareResult(0.0, face_count, None)

        # Ảnh reference cũng cần đúng 1 mặt. Nếu enroll kém (0 hoặc nhiều mặt) → KHÔNG raise
        # (tránh 502 chặn cả face-check), trả score 0.0 KÈM số mặt đọc được trên ảnh mốc để
        # caller phân biệt "mốc hỏng" (→ identity_unverified) với "người khác" (→ face_mismatch).
        ref_face_count, ref_emb = self._reference_faces(ref_bytes)
        if ref_face_count != 1 or ref_emb is None:
            return FaceCompareResult(0.0, face_count, ref_face_count)

        live_emb = live_faces[0].normed_embedding
        return FaceCompareResult(self._cosine(ref_emb, live_emb), face_count, ref_face_count)

    @staticmethod
    def _cosine(a, b) -> float:
        import numpy as np

        a = np.asarray(a, dtype=np.float32)
        b = np.asarray(b, dtype=np.float32)
        denom = float(np.linalg.norm(a) * np.linalg.norm(b))
        if denom == 0.0:
            return 0.0
        return float(np.dot(a, b) / denom)
