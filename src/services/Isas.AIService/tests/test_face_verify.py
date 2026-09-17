# tests/test_face_verify.py — SEC-2/3: POST /face-verify (đối chiếu khuôn mặt + đếm mặt)
#
# insightface/cv2/numpy được STUB trong conftest (như faster_whisper) → pytest chạy KHÔNG
# cần cài ML deps (insightface/onnxruntime/opencv). Test verify LOGIC signals/match/score ở
# endpoint bằng cách monkeypatch FaceVerifier.compare + storage.get_object_bytes — không
# gọi model detect/embed thật, không đụng S3.
#
# GEN-7: /face-verify nay gate X-Internal-Token (fail-closed) như /decide-next → mọi call hợp lệ
# phải kèm _HEADERS; xem test_endpoint_requires_internal_token cho nhánh 401.
import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.config import settings
from app.face_verify import FaceCompareResult

client = TestClient(main_module.app)

_KEYS = {"referenceImageKey": "ref.jpg", "liveImageKey": "live.jpg"}
_HEADERS = {"X-Internal-Token": settings.internal_token}


def _stub_io(monkeypatch, score, face_count, ref_face_count=1):
    """Chặn S3 + model: get_object_bytes trả bytes giả, compare trả kết quả cố định.

    Nhận 3 tham số rời thay vì một tuple (như trước): `compare` nay trả FaceCompareResult có
    thêm `reference_face_count`, và chính trường đó là thứ phân biệt "ảnh mốc hỏng" với
    "người khác". Mặc định 1 = ảnh mốc bình thường, để các ca cũ đọc y như trước.
    """
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"fake-image-bytes")
    monkeypatch.setattr(
        main_module.face_verifier, "compare",
        lambda ref, live: FaceCompareResult(score, face_count, ref_face_count))


def test_no_face_gives_no_face_signal_and_no_match(monkeypatch):
    # ref_face_count=None: live đã không so được nên compare thật KHÔNG detect ảnh mốc.
    _stub_io(monkeypatch, 0.0, 0, None)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 0
    assert body["match"] is False
    assert body["signals"] == ["no_face"]


def test_multiple_faces_gives_multiple_faces_signal(monkeypatch):
    _stub_io(monkeypatch, 0.0, 3, None)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 3
    assert body["match"] is False
    assert body["signals"] == ["multiple_faces"]


def test_one_face_high_score_matches(monkeypatch):
    _stub_io(monkeypatch, 0.82, 1)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json={**_KEYS, "threshold": 0.4})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1
    assert body["match"] is True
    assert body["signals"] == []
    assert body["score"] == pytest.approx(0.82)


def test_one_face_low_score_is_face_mismatch(monkeypatch):
    _stub_io(monkeypatch, 0.15, 1)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json={**_KEYS, "threshold": 0.4})
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1
    assert body["match"] is False
    assert body["signals"] == ["face_mismatch"]


def test_anh_moc_khong_doc_duoc_mat_ra_identity_unverified(monkeypatch):
    """Ảnh MỐC 0 mặt (enroll trúng khung đen) → identity_unverified, TUYỆT ĐỐI không face_mismatch.

    Đây là ca đã xảy ra thật trên prod 2026-08-08: webcam chưa phơi sáng, FE upload ảnh đen làm
    mốc ⇒ mọi lượt so đều score 0.0 ⇒ ứng viên trung thực bị gắn "không đúng người" mỗi 30 giây.
    Lỗi nằm ở ảnh mốc, không phải ở người đang ngồi trước camera."""
    _stub_io(monkeypatch, 0.0, 1, 0)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 200
    body = res.json()
    assert body["faceCount"] == 1          # live vẫn thấy đúng 1 người
    assert body["match"] is False
    assert body["signals"] == ["identity_unverified"]
    assert "face_mismatch" not in body["signals"]


def test_anh_moc_nhieu_mat_cung_ra_identity_unverified(monkeypatch):
    # Enroll trúng lúc có người đi ngang → ảnh mốc 2 mặt: cũng không so khớp được, cũng không
    # phải lỗi ứng viên.
    _stub_io(monkeypatch, 0.0, 1, 2)
    body = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS).json()
    assert body["signals"] == ["identity_unverified"]
    assert body["match"] is False


def test_anh_moc_hong_thang_truoc_ca_score_cao(monkeypatch):
    """Khoá THỨ TỰ nhánh: mốc hỏng thì không được "match" dù score tình cờ cao.

    Không có test này thì đổi thứ tự hai nhánh (xét score trước) vẫn xanh, mà hậu quả là một
    ảnh mốc vô nghĩa lại cho ra kết luận "đúng người" — tệ hơn cả bug đang sửa."""
    _stub_io(monkeypatch, 0.99, 1, 0)
    body = client.post("/api/v1/face-verify", headers=_HEADERS, json={**_KEYS, "threshold": 0.4}).json()
    assert body["match"] is False
    assert body["signals"] == ["identity_unverified"]


def test_default_threshold_used_when_omitted(monkeypatch):
    # score 0.5 ≥ default face_match_threshold (0.4) → match: chứng minh dùng ngưỡng mặc định.
    _stub_io(monkeypatch, 0.5, 1)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 200
    assert res.json()["match"] is True


def test_score_exactly_at_threshold_matches(monkeypatch):
    # Biên: score == threshold → khớp (≥, không phải >).
    _stub_io(monkeypatch, 0.4, 1)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json={**_KEYS, "threshold": 0.4})
    assert res.json()["match"] is True
    assert res.json()["signals"] == []


def test_missing_required_key_rejected():
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json={"liveImageKey": "live.jpg"})
    assert res.status_code == 422  # referenceImageKey bắt buộc (pydantic)


def test_blank_key_rejected(monkeypatch):
    _stub_io(monkeypatch, 0.9, 1)
    res = client.post("/api/v1/face-verify", headers=_HEADERS,
                      json={"referenceImageKey": "   ", "liveImageKey": "live.jpg"})
    assert res.status_code == 400


def test_502_when_model_fails(monkeypatch):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"x")

    def boom(ref, live):
        raise RuntimeError("model down")

    monkeypatch.setattr(main_module.face_verifier, "compare", boom)
    res = client.post("/api/v1/face-verify", headers=_HEADERS, json=_KEYS)
    assert res.status_code == 502
    assert "Lỗi đối chiếu khuôn mặt" in res.json()["detail"]


# ── FaceVerifier.compare — chạy CODE THẬT (chỉ giả lớp detect) ───────────────────
#
# Mọi test phía trên monkeypatch nguyên hàm `compare`, nên chúng KHÔNG hề chứng minh
# `reference_face_count` được tính đúng. Mutation "cho reference_face_count luôn = 1" chạy qua
# 545 test vẫn XANH — tức bug ảnh-mốc-hỏng có thể quay lại y nguyên mà không ai biết. Nhóm test
# dưới đây đóng đúng khe đó: giả `_detect` (khỏi cần model), gọi `compare` thật.


class _FakeFace:
    def __init__(self, emb):
        self.normed_embedding = emb


def _verifier_with(monkeypatch, ref_faces, live_faces):
    from app.face_verify import FaceVerifier

    v = FaceVerifier()
    seen: list[bytes] = []

    def fake_detect(img_bytes):
        seen.append(img_bytes)
        return live_faces if img_bytes == b"live" else ref_faces

    monkeypatch.setattr(v, "_detect", fake_detect)
    return v, seen


def test_compare_bao_cao_so_mat_cua_anh_moc(monkeypatch):
    v, _ = _verifier_with(monkeypatch, ref_faces=[_FakeFace([1.0, 0.0])],
                          live_faces=[_FakeFace([1.0, 0.0])])
    r = v.compare(b"ref", b"live")
    assert r.face_count == 1
    assert r.reference_face_count == 1
    assert r.score == pytest.approx(1.0)


def test_compare_anh_moc_khong_co_mat_bao_0_khong_phai_1(monkeypatch):
    """Ca sinh ra bản vá: mốc 0 mặt phải BÁO ĐÚNG 0 để endpoint ra identity_unverified."""
    v, _ = _verifier_with(monkeypatch, ref_faces=[], live_faces=[_FakeFace([1.0, 0.0])])
    r = v.compare(b"ref", b"live")
    assert r.reference_face_count == 0
    assert r.score == 0.0
    assert r.face_count == 1


def test_compare_anh_moc_nhieu_mat_bao_dung_so(monkeypatch):
    v, _ = _verifier_with(monkeypatch,
                          ref_faces=[_FakeFace([1.0, 0.0]), _FakeFace([0.0, 1.0])],
                          live_faces=[_FakeFace([1.0, 0.0])])
    assert v.compare(b"ref", b"live").reference_face_count == 2


def test_compare_live_hong_thi_chua_xet_anh_moc(monkeypatch):
    """live 0 mặt → reference_face_count = None ("chưa nhìn"), và KHÔNG tốn lượt detect ảnh mốc.

    None chứ không phải 0: 0 nghĩa là "đã nhìn và không thấy mặt nào" — hai chuyện khác nhau,
    và chính chỗ đó quyết định endpoint gắn no_face hay identity_unverified."""
    v, seen = _verifier_with(monkeypatch, ref_faces=[_FakeFace([1.0, 0.0])], live_faces=[])
    r = v.compare(b"ref", b"live")
    assert r.reference_face_count is None
    assert r.face_count == 0
    assert seen == [b"live"]  # không đụng ảnh mốc


def test_endpoint_requires_internal_token(monkeypatch):
    """GEN-7: thiếu / sai X-Internal-Token → 401 (fail-closed), TRƯỚC cả khi chạm S3/model.

    Gate nằm đầu hàm nên body hợp lệ vẫn 401; và stub_io chứng minh không hề gọi model khi bị chặn."""
    called = {"io": False}

    def spy(key):
        called["io"] = True
        return b"x"

    monkeypatch.setattr(main_module.storage, "get_object_bytes", spy)

    res_missing = client.post("/api/v1/face-verify", json=_KEYS)
    assert res_missing.status_code == 401

    res_wrong = client.post("/api/v1/face-verify",
                            headers={"X-Internal-Token": "wrong-token"}, json=_KEYS)
    assert res_wrong.status_code == 401

    assert called["io"] is False  # bị chặn trước khi kéo ảnh S3


# ---------------------------------------------------------------------------------------------
# Cache vector ảnh mốc (2026-09-17, hạ nhịp kiểm mặt 30s→15s). Khoá theo NỘI DUNG ảnh, không theo
# S3 key — enroll lại ghi đè cùng key nên khoá theo key sẽ đem ảnh mốc mới so bằng vector cũ.


def test_anh_moc_cung_noi_dung_chi_detect_mot_lan(monkeypatch):
    v, seen = _verifier_with(monkeypatch, ref_faces=[_FakeFace([1.0, 0.0])],
                             live_faces=[_FakeFace([1.0, 0.0])])
    r1 = v.compare(b"ref", b"live")
    r2 = v.compare(b"ref", b"live")
    r3 = v.compare(b"ref", b"live")
    assert (r1.score, r2.score, r3.score) == (pytest.approx(1.0),) * 3
    assert (r1.reference_face_count, r3.reference_face_count) == (1, 1)
    # 3 lượt live + đúng 1 lượt mốc — lượt 2/3 lấy từ cache.
    assert seen.count(b"ref") == 1
    assert seen.count(b"live") == 3


def test_anh_moc_doi_noi_dung_thi_detect_lai(monkeypatch):
    """Enroll lại (cùng key S3, khác byte) phải cho vector MỚI — đây là lý do khoá theo nội dung."""
    from app.face_verify import FaceVerifier

    v = FaceVerifier()
    seen: list[bytes] = []

    def fake_detect(img_bytes):
        seen.append(img_bytes)
        if img_bytes == b"live":
            return [_FakeFace([1.0, 0.0])]
        if img_bytes == b"ref-dark":
            return []  # mốc đen: 0 mặt
        return [_FakeFace([1.0, 0.0])]  # b"ref-good"

    monkeypatch.setattr(v, "_detect", fake_detect)
    bad = v.compare(b"ref-dark", b"live")
    good = v.compare(b"ref-good", b"live")
    assert bad.reference_face_count == 0 and bad.score == 0.0
    assert good.reference_face_count == 1 and good.score == pytest.approx(1.0)
    assert seen.count(b"ref-dark") == 1 and seen.count(b"ref-good") == 1


def test_anh_moc_hong_cung_duoc_nho(monkeypatch):
    """Mốc 0 mặt bị hỏi lại mỗi 10s (nhịp báo động) — không detect lại, vẫn báo đúng 0."""
    v, seen = _verifier_with(monkeypatch, ref_faces=[], live_faces=[_FakeFace([1.0, 0.0])])
    for _ in range(3):
        r = v.compare(b"ref", b"live")
        assert r.reference_face_count == 0 and r.score == 0.0
    assert seen.count(b"ref") == 1


def test_detect_nem_thi_khong_cache(monkeypatch):
    from app.face_verify import FaceVerifier

    v = FaceVerifier()
    calls = {"n": 0}

    def fake_detect(img_bytes):
        if img_bytes == b"live":
            return [_FakeFace([1.0, 0.0])]
        calls["n"] += 1
        if calls["n"] == 1:
            raise RuntimeError("model tạm hỏng")
        return [_FakeFace([1.0, 0.0])]

    monkeypatch.setattr(v, "_detect", fake_detect)
    with pytest.raises(RuntimeError):
        v.compare(b"ref", b"live")
    assert v.compare(b"ref", b"live").score == pytest.approx(1.0)
    assert calls["n"] == 2


def test_cache_co_tran(monkeypatch):
    from app.face_verify import FaceVerifier

    v = FaceVerifier()
    monkeypatch.setattr(v, "_REF_CACHE_MAX", 2)
    monkeypatch.setattr(v, "_detect", lambda b: [_FakeFace([1.0, 0.0])])
    for k in (b"r1", b"r2", b"r3"):
        v.compare(k, b"live")
    assert len(v._ref_cache) == 2
