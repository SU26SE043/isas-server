# tests/test_face_verify.py — SEC-2/3: POST /face-verify (đối chiếu khuôn mặt + đếm mặt)
#
# insightface/cv2/numpy được STUB trong conftest (như faster_whisper) → pytest chạy KHÔNG
# cần cài ML deps (insightface/onnxruntime/opencv). Test verify LOGIC signals/match/score ở
# endpoint bằng cách monkeypatch FaceVerifier.compare + storage.get_object_bytes — không
# gọi model detect/embed thật, không đụng S3.
import pytest
from fastapi.testclient import TestClient

import app.main as main_module

client = TestClient(main_module.app)

_KEYS = {"referenceImageKey": "ref.jpg", "liveImageKey": "live.jpg"}


def _stub_io(monkeypatch, compare_return):
    """Chặn S3 + model: get_object_bytes trả bytes giả, compare trả (score, faceCount) cố định."""
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"fake-image-bytes")
    monkeypatch.setattr(main_module.face_verifier, "compare", lambda ref, live: compare_return)


def test_no_face_gives_no_face_signal_and_no_match(monkeypatch):
    _stub_io(monkeypatch, (0.0, 0))
    res = client.post("/api/v1/face-verify", json=_KEYS)
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 0
    assert body["match"] is False
    assert body["signals"] == ["no_face"]


def test_multiple_faces_gives_multiple_faces_signal(monkeypatch):
    _stub_io(monkeypatch, (0.0, 3))
    res = client.post("/api/v1/face-verify", json=_KEYS)
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 3
    assert body["match"] is False
    assert body["signals"] == ["multiple_faces"]


def test_one_face_high_score_matches(monkeypatch):
    _stub_io(monkeypatch, (0.82, 1))
    res = client.post("/api/v1/face-verify", json={**_KEYS, "threshold": 0.4})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1
    assert body["match"] is True
    assert body["signals"] == []
    assert body["score"] == pytest.approx(0.82)


def test_one_face_low_score_is_face_mismatch(monkeypatch):
    _stub_io(monkeypatch, (0.15, 1))
    res = client.post("/api/v1/face-verify", json={**_KEYS, "threshold": 0.4})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1
    assert body["match"] is False
    assert body["signals"] == ["face_mismatch"]


def test_default_threshold_used_when_omitted(monkeypatch):
    # score 0.5 ≥ default face_match_threshold (0.4) → match: chứng minh dùng ngưỡng mặc định.
    _stub_io(monkeypatch, (0.5, 1))
    res = client.post("/api/v1/face-verify", json=_KEYS)
    assert res.status_code == 200
    assert res.json()["match"] is True


def test_score_exactly_at_threshold_matches(monkeypatch):
    # Biên: score == threshold → khớp (≥, không phải >).
    _stub_io(monkeypatch, (0.4, 1))
    res = client.post("/api/v1/face-verify", json={**_KEYS, "threshold": 0.4})
    assert res.json()["match"] is True
    assert res.json()["signals"] == []


def test_missing_required_key_rejected():
    res = client.post("/api/v1/face-verify", json={"liveImageKey": "live.jpg"})
    assert res.status_code == 422  # referenceImageKey bắt buộc (pydantic)


def test_blank_key_rejected(monkeypatch):
    _stub_io(monkeypatch, (0.9, 1))
    res = client.post("/api/v1/face-verify",
                      json={"referenceImageKey": "   ", "liveImageKey": "live.jpg"})
    assert res.status_code == 400


def test_502_when_model_fails(monkeypatch):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"x")

    def boom(ref, live):
        raise RuntimeError("model down")

    monkeypatch.setattr(main_module.face_verifier, "compare", boom)
    res = client.post("/api/v1/face-verify", json=_KEYS)
    assert res.status_code == 502
    assert "Lỗi đối chiếu khuôn mặt" in res.json()["detail"]
