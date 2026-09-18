# tests/test_face_detect.py — B2C coaching: POST /face-detect (ĐẾM MẶT, không so khớp danh tính)
#
# Khác /face-verify: 1 ảnh duy nhất, không ảnh tham chiếu, không score/match, không
# face_mismatch — chỉ no_face/multiple_faces. Người luyện tự bật (BC-6 ngoại lệ), chỉ chính
# họ đọc kết quả.
import pathlib
import re

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.config import settings

client = TestClient(main_module.app)

_HEADERS = {"X-Internal-Token": settings.internal_token}


def _stub_io(monkeypatch, face_count):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"fake-image-bytes")
    monkeypatch.setattr(main_module.face_verifier, "count_faces", lambda img: face_count)


def test_requires_internal_token():
    res = client.post("/api/v1/face-detect", json={"imageKey": "x.jpg"})
    assert res.status_code == 401


def test_empty_image_key_rejected():
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "  "})
    assert res.status_code == 400


def test_missing_image_key_is_422():
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={})
    assert res.status_code == 422


def test_zero_faces_gives_no_face_signal(monkeypatch):
    _stub_io(monkeypatch, 0)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 0
    assert body["signals"] == ["no_face"]


def test_one_face_gives_no_signal(monkeypatch):
    _stub_io(monkeypatch, 1)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1
    assert body["signals"] == []


def test_multiple_faces_gives_multiple_faces_signal(monkeypatch):
    _stub_io(monkeypatch, 3)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 3
    assert body["signals"] == ["multiple_faces"]


def test_no_face_mismatch_signal_ever_appears(monkeypatch):
    """Detect-only: KHÔNG có ảnh tham chiếu nên KHÔNG BAO GIỜ được sinh `face_mismatch`."""
    for count in (0, 1, 2, 5):
        _stub_io(monkeypatch, count)
        res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
        assert "face_mismatch" not in res.json()["signals"]
        assert "match" not in res.json()
        assert "score" not in res.json()


def test_count_faces_raises_gives_502(monkeypatch):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"fake-image-bytes")

    def _boom(img):
        raise RuntimeError("model lỗi")

    monkeypatch.setattr(main_module.face_verifier, "count_faces", _boom)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 502


class _NotFoundError(Exception):
    def __init__(self):
        self.response = {
            "Error": {"Code": "NoSuchKey"},
            "ResponseMetadata": {"HTTPStatusCode": 404},
        }


def test_image_not_found_gives_404_with_bucket(monkeypatch):
    def _raise_not_found(key):
        raise _NotFoundError()

    monkeypatch.setattr(main_module.storage, "get_object_bytes", _raise_not_found)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "missing.jpg"})
    assert res.status_code == 404
    assert settings.s3_bucket in res.json()["detail"]


def test_other_s3_error_gives_502_not_404(monkeypatch):
    def _raise_other(key):
        raise RuntimeError("mạng lỗi")

    monkeypatch.setattr(main_module.storage, "get_object_bytes", _raise_other)
    res = client.post("/api/v1/face-detect", headers=_HEADERS, json={"imageKey": "a.jpg"})
    assert res.status_code == 502


# ── HỢP ĐỒNG CHÉO với .NET ───────────────────────────────────────────────────────────
def _interview_src(*parts: str) -> str:
    path = pathlib.Path(__file__).resolve().parents[2] / "Isas.InterviewService"
    for p in parts:
        path = path / p
    return path.read_text(encoding="utf-8")


def test_khoa_face_detect_khop_dto_dotnet():
    """Đường dẫn/khoá JSON/tên property của AiServiceFaceDetector.cs phải khớp endpoint này.

    Đúng lớp bug đã cắn repo nhiều lần: khoá JSON lệch giữa .NET và Python thì field rụng im
    lặng (pydantic `extra='ignore'` phía nhận, .NET bind hụt phía gửi) — không exception nào,
    chỉ dữ liệu biến mất."""
    src = _interview_src("Services", "AiServiceFaceDetector.cs")
    assert "/api/v1/face-detect" in src
    assert '"imageKey"' in src or "imageKey" in src
    assert re.search(r"\bFaceCount\b", src)
    assert re.search(r"\bSignals\b", src)
