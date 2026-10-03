# tests/test_face_unreadable.py — ảnh KHÔNG giải mã được ⇒ 422 (lỗi DỮ LIỆU), không phải 502 (hạ tầng).
#
# Dev 03/10: 4 byte đầu JPEG + 4000 byte rác gửi vào Campaign face-enroll → AIService /face-detect
# trả 502 → Campaign FAIL-OPEN nhận luôn mốc rác (204) → mọi lượt face-check bằng người thật sau đó
# cũng 502 → session_flags 0 dòng. Hai lỗi khác bản chất bị gộp làm một mã: "AIService của ta hỏng"
# (không được phạt ứng viên — SEC-5) và "ảnh gửi vào là rác" (phải NÓI RA). Ở đây khoá:
#   • ảnh rác → 422, detail OBJECT {code:"IMAGE_UNREADABLE", image, message} ở CẢ HAI endpoint;
#   • `image` nói đúng ảnh nào hỏng (reference | live | image);
#   • lỗi khác vẫn 502 — hợp đồng THÊM, không bỏ;
#   • đường giải mã THẬT (cv2) chứ không chỉ monkeypatch;
#   • hợp đồng chéo với Campaign (.NET): test_face_unreadable_contract.py.
import importlib.util

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.config import settings
from app.face_verify import IMAGE_UNREADABLE_CODE, FaceVerifier, UnreadableImageError

client = TestClient(main_module.app)

_HEADERS = {"X-Internal-Token": settings.internal_token}
_KEYS = {"referenceImageKey": "ref.jpg", "liveImageKey": "live.jpg"}

# Đúng hình dạng request đã tái hiện bug trên dev: chữ ký JPEG + rác.
_JUNK = b"\xff\xd8\xff\xe0" + bytes((i * 37 + 11) % 256 for i in range(4000))


def _assert_unreadable(res, image: str) -> None:
    assert res.status_code == 422, res.text
    detail = res.json()["detail"]
    # OBJECT, không phải MẢNG — đúng thứ Campaign dùng để tách khỏi 422 pydantic.
    assert isinstance(detail, dict)
    assert detail["code"] == IMAGE_UNREADABLE_CODE == "IMAGE_UNREADABLE"
    assert detail["image"] == image
    assert detail["message"]


# ── /face-detect ─────────────────────────────────────────────────────────────────
def test_face_detect_anh_rac_422_image(monkeypatch):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: _JUNK)

    def _raise(img):
        raise UnreadableImageError()

    monkeypatch.setattr(main_module.face_verifier, "count_faces", _raise)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    _assert_unreadable(res, "image")


def test_face_detect_loi_khac_van_502(monkeypatch):
    """ValueError KHÔNG phải UnreadableImageError (vd lỗi model) vẫn là hạ tầng → 502."""
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"x")

    def _raise(img):
        raise ValueError("model trả shape lạ")

    monkeypatch.setattr(main_module.face_verifier, "count_faces", _raise)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 502


# ── /face-verify ─────────────────────────────────────────────────────────────────
@pytest.mark.parametrize("image", ["reference", "live"])
def test_face_verify_anh_rac_422_noi_dung_anh_nao(monkeypatch, image):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"x")

    def _raise(ref, live):
        raise UnreadableImageError(image)

    monkeypatch.setattr(main_module.face_verifier, "compare", _raise)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    _assert_unreadable(res, image)


def test_face_verify_loi_khac_van_502(monkeypatch):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"x")

    def _raise(ref, live):
        raise RuntimeError("model down")

    monkeypatch.setattr(main_module.face_verifier, "compare", _raise)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 502


# ── compare THẬT: gắn đúng tên ảnh hỏng ──────────────────────────────────────────
class _FakeFace:
    def __init__(self, emb):
        self.normed_embedding = emb


def _verifier_detect(monkeypatch, broken: bytes):
    """compare thật, `_detect` giả: ảnh `broken` ném như _decode thật, ảnh khác có đúng 1 mặt."""
    v = FaceVerifier()

    def fake_detect(img_bytes):
        if img_bytes == broken:
            raise UnreadableImageError()
        return [_FakeFace([1.0, 0.0])]

    monkeypatch.setattr(v, "_detect", fake_detect)
    return v


def test_compare_live_rac_bao_live(monkeypatch):
    v = _verifier_detect(monkeypatch, broken=b"live")
    with pytest.raises(UnreadableImageError) as exc:
        v.compare(b"ref", b"live")
    assert exc.value.image == "live"


def test_compare_moc_rac_bao_reference(monkeypatch):
    v = _verifier_detect(monkeypatch, broken=b"ref")
    with pytest.raises(UnreadableImageError) as exc:
        v.compare(b"ref", b"live")
    assert exc.value.image == "reference"


def test_compare_moc_rac_khong_bi_cache(monkeypatch):
    """Ảnh mốc rác KHÔNG được nhớ như kết luận "0 mặt" — nhớ nhầm thì lượt sau ra identity_unverified
    kiểu "mốc đọc được, 0 mặt" thay vì 422, mất mã nói rõ nguyên nhân."""
    v = _verifier_detect(monkeypatch, broken=b"ref")
    for _ in range(2):
        with pytest.raises(UnreadableImageError):
            v.compare(b"ref", b"live")
    assert len(v._ref_cache) == 0


# ── Đường giải mã THẬT (cv2) — không monkeypatch _decode ─────────────────────────
# skipif theo TỪNG test, không importorskip cấp module: thiếu cv2 thì chỉ nhóm này bỏ qua, nhóm
# monkeypatch phía trên vẫn chạy. CI cài requirements.txt (opencv-python-headless) nên nhóm này CÓ chạy.
_needs_cv2 = pytest.mark.skipif(
    importlib.util.find_spec("cv2") is None or importlib.util.find_spec("numpy") is None,
    reason="cần opencv + numpy để giải mã ảnh thật")


def _real_jpeg() -> bytes:
    import cv2
    import numpy as np

    img = np.zeros((32, 32, 3), dtype=np.uint8)
    img[8:24, 8:24] = (200, 180, 160)
    ok, buf = cv2.imencode(".jpg", img)
    assert ok
    return buf.tobytes()


@_needs_cv2
@pytest.mark.parametrize("data", [_JUNK, b"hello", b""], ids=["jpeg-header-rac", "text", "rong"])
def test_decode_that_anh_rac_nem_unreadable(data):
    with pytest.raises(UnreadableImageError):
        FaceVerifier()._decode(data)


@_needs_cv2
def test_decode_that_anh_hop_le_khong_nem():
    assert FaceVerifier()._decode(_real_jpeg()).shape == (32, 32, 3)


class _OneFaceModel:
    """Model giả nhận ndarray ĐÃ giải mã thật, luôn thấy đúng 1 mặt."""

    def get(self, img):
        assert img is not None and img.ndim == 3
        return [_FakeFace([1.0, 0.0])]


def _endpoint_verifier(monkeypatch, objects: dict[str, bytes]):
    v = FaceVerifier()
    v._model_instance = _OneFaceModel()
    monkeypatch.setattr(main_module, "face_verifier", v)
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: objects[key])


@_needs_cv2
def test_face_detect_dau_cuoi_anh_rac_422(monkeypatch):
    _endpoint_verifier(monkeypatch, {"a.jpg": _JUNK})
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    _assert_unreadable(res, "image")


@_needs_cv2
def test_face_detect_dau_cuoi_anh_hop_le_khong_doi(monkeypatch):
    _endpoint_verifier(monkeypatch, {"a.jpg": _real_jpeg()})
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 200
    assert res.json() == {"faceCount": 1, "signals": []}


@_needs_cv2
def test_face_verify_dau_cuoi_moc_rac_422_reference(monkeypatch):
    _endpoint_verifier(monkeypatch, {"ref.jpg": _JUNK, "live.jpg": _real_jpeg()})
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    _assert_unreadable(res, "reference")


@_needs_cv2
def test_face_verify_dau_cuoi_live_rac_422_live(monkeypatch):
    _endpoint_verifier(monkeypatch, {"ref.jpg": _real_jpeg(), "live.jpg": _JUNK})
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    _assert_unreadable(res, "live")


@_needs_cv2
def test_face_verify_dau_cuoi_anh_hop_le_khong_doi(monkeypatch):
    _endpoint_verifier(monkeypatch, {"ref.jpg": _real_jpeg(), "live.jpg": _real_jpeg()})
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1 and body["match"] is True and body["signals"] == []
