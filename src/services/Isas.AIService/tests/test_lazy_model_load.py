"""Model nặng phải nạp LƯỜI — và chỉ nạp MỘT lần dù bị gọi đồng thời.

Vì sao đáng khoá bằng test: đây là hai dòng rất dễ bị "sửa cho gọn" trở lại `__init__` (bản cũ
chính là thế, và comment cũ còn ghi "load 1 lần trong __init__" như một chủ ý). Nếu ai đó làm vậy
thì KHÔNG có gì hỏng, không test nào khác đỏ — chỉ là RAM thường trú tăng lại ~1,1 GB trên box
7,6 GB, phát hiện ra lúc thread pool bắt đầu bị OOM. Đo trong image (`ru_maxrss`):

    python trần        12 MB
    + WhisperModel    778 MB   ← nạp ở CẢ api LẪN worker
    + FaceAnalysis   1136 MB   ← chỉ api

`conftest.py` đã stub `faster_whisper` và `insightface` nên ở đây chỉ cần đếm số lần ctor được gọi.
"""
import threading
import time

import pytest

from app import face_verify as fv_mod
from app import transcriber as tr_mod


# ── Whisper ─────────────────────────────────────────────────────────────────────────
def _count_whisper(monkeypatch):
    calls = []

    class _Model:
        def __init__(self, *a, **k):
            calls.append(a)

    monkeypatch.setattr(tr_mod, "WhisperModel", _Model)
    return calls


def test_transcriber_khong_nap_model_luc_dung_object(monkeypatch):
    calls = _count_whisper(monkeypatch)

    tr_mod.Transcriber()

    assert calls == [], "dựng Transcriber KHÔNG được chạm WhisperModel — đó là 778 MB"


def test_transcriber_nap_o_lan_dung_dau_va_chi_mot_lan(monkeypatch):
    calls = _count_whisper(monkeypatch)
    t = tr_mod.Transcriber()

    first, second = t._model, t._model

    assert len(calls) == 1, "phải nạp đúng 1 lần rồi dùng lại"
    assert first is second


def test_transcriber_tam_thread_cung_luc_van_chi_MOT_model(monkeypatch):
    """`_transcribe_local` chạy trong thread của `asyncio.to_thread`.

    Không khoá thì N lượt dự phòng đồng thời dựng N model — và dự phòng chỉ kích hoạt khi nhà
    cung cấp từ xa ĐANG hỏng, tức đúng lúc hệ chịu tải nhất.
    """
    calls = []
    started = threading.Barrier(8)

    class _SlowModel:
        def __init__(self, *a, **k):
            calls.append(a)
            # 🔴 `sleep` là PHẦN THIẾT YẾU của test, không phải cho có.
            # Ctor rỗng chạy xong trong một khe GIL (mặc định 5 ms) nên không thread nào kịp
            # chen vào giữa "kiểm None" và "gán" ⇒ gỡ khoá đi test VẪN XANH, và ta tưởng đã
            # phủ. `WhisperModel` thật mất vài GIÂY — đó mới là cửa sổ đua có thật. `sleep`
            # nhả GIL, tái hiện đúng cửa sổ đó ở quy mô mili-giây.
            time.sleep(0.05)

    monkeypatch.setattr(tr_mod, "WhisperModel", _SlowModel)
    t = tr_mod.Transcriber()
    seen = []

    def grab():
        started.wait()          # ép 8 thread cùng vào một lúc, không tuần tự hoá ngẫu nhiên
        seen.append(t._model)

    threads = [threading.Thread(target=grab) for _ in range(8)]
    for th in threads:
        th.start()
    for th in threads:
        th.join()

    assert len(calls) == 1, f"8 thread đồng thời phải chỉ nạp 1 model, thực tế {len(calls)}"
    assert len(set(map(id, seen))) == 1, "mọi thread phải thấy CÙNG một instance"


# ── InsightFace ─────────────────────────────────────────────────────────────────────
def _count_face(monkeypatch):
    calls = []

    class _Face:
        def __init__(self, *a, **k):
            calls.append(k)

        def prepare(self, **k):
            calls.append(("prepare", k))

    monkeypatch.setattr(fv_mod, "FaceAnalysis", _Face)
    return calls


def test_faceverifier_khong_nap_model_luc_dung_object(monkeypatch):
    calls = _count_face(monkeypatch)

    fv_mod.FaceVerifier()

    assert calls == [], "dựng FaceVerifier KHÔNG được chạm FaceAnalysis — đó là 358 MB"


def test_faceverifier_nap_o_lan_dung_dau_va_chi_mot_lan(monkeypatch):
    calls = _count_face(monkeypatch)
    f = fv_mod.FaceVerifier()

    first, second = f._model, f._model

    # 1 lần ctor + 1 lần prepare, không hơn.
    assert len(calls) == 2, f"kỳ vọng ctor+prepare đúng 1 lượt, thực tế {calls}"
    assert first is second


def test_faceverifier_prepare_nem_thi_KHONG_giu_model_hong(monkeypatch):
    """Gán instance trước khi `prepare()` xong sẽ để request kế đọc được model chưa prepare —
    lỗi biểu hiện ra ở chỗ khác hẳn nguyên nhân, rất khó lần."""
    class _Broken:
        def __init__(self, *a, **k):
            pass

        def prepare(self, **k):
            raise RuntimeError("tải model hỏng")

    monkeypatch.setattr(fv_mod, "FaceAnalysis", _Broken)
    f = fv_mod.FaceVerifier()

    with pytest.raises(RuntimeError):
        _ = f._model

    assert f._model_instance is None, "prepare hỏng thì không được giữ lại model dở dang"
