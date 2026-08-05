# tests/test_transcript_engine_wire.py — CON DẤU ENGINE đi ra ngoài dưới đúng khoá `transcriptEngine`.
#
# 🔴 VÌ SAO TÁCH RIÊNG MỘT FILE CHO ĐÚNG MỘT TÊN KHOÁ: đây là hợp đồng DÂY giữa Python và .NET, và
# lệch tên ở đây KHÔNG NÉM LỖI — nó chỉ làm .NET bind hụt rồi lưu NULL vĩnh viễn. Repo đã dính đúng
# lớp bug này ba lần: `focusCriteria` bị pydantic `extra='ignore'` nuốt (BC14 hỏng âm thầm), và
# `adaptiveMaxQuestions` vs `maxQuestions` (mọi user nhận trần 0 câu). Cả hai bên đều xanh test vì
# mỗi bên chỉ khẳng định hợp đồng của CHÍNH MÌNH.
#
# Con dấu này cần thiết vì đường chép lời nay có DỰ PHÒNG: nhà cung cấp từ xa hỏng thì bản chép
# lặng lẽ rơi về Whisper cục bộ (lỗi từ 4,2% so với 0,7%) mà nhìn từ ngoài hai bản không phân biệt
# được. Thiếu con dấu ⇒ "điểm thấp này do ứng viên hay do bản chép?" là câu không trả lời được.
from unittest.mock import AsyncMock

from fastapi.testclient import TestClient

import app.main as main_module
from app import worker
from app.config import settings
from app.fluency import Segment, compute_delivery_metrics
from app.transcriber import TranscriptionResult

client = TestClient(main_module.app)
_HEADERS = {"X-Internal-Token": settings.internal_token}
_CRITERIA = [{"name": "Kiến thức kỹ thuật", "description": "Hiểu khái niệm cốt lõi"}]

KEY = "transcriptEngine"   # 🔴 đổi hằng này = đổi hợp đồng với .NET. BÁO, đừng tự sửa một bên.


def _result(text="transcript từ audio", engine="whisper-1"):
    return TranscriptionResult(
        text=text,
        metrics=compute_delivery_metrics(text, [Segment(0.0, 2.0, text)], 3.0),
        engine=engine,
    )


# ── /decide-next ─────────────────────────────────────────────────────────────────────
def _call_decide_next(monkeypatch, result):
    monkeypatch.setattr(main_module.storage, "get_object_bytes", lambda key: b"fake-audio")
    monkeypatch.setattr(main_module.transcriber, "transcribe_detailed",
                        lambda path, lang="vi": result)

    async def fake_decide(**kwargs):
        return {"action": "follow_up", "nextQuestion": "Đào sâu?", "reason": "r"}

    monkeypatch.setattr(main_module.provider, "decide_next", fake_decide)

    return client.post("/api/v1/decide-next", headers=_HEADERS, json={
        "jobCategory": "FE",
        "audioObjectKey": "answer-audio/u/a1.webm",
        "currentQuestion": "Q",
        "criteria": _CRITERIA,
    })


def test_decide_next_tra_con_dau_engine(monkeypatch):
    """Buổi THÍCH ỨNG chép lời ĐÚNG MỘT LẦN ở đây (worker bỏ Whisper khi job đã mang transcript)
    ⇒ con dấu không đi ra ở đây thì nó không còn cơ hội nào khác."""
    res = _call_decide_next(monkeypatch, _result(engine="gemini-2.5-flash"))

    assert res.status_code == 200
    body = res.json()
    assert KEY in body, f"thiếu khoá {KEY!r} ⇒ .NET lưu NULL vĩnh viễn mà KHÔNG lỗi gì"
    assert body[KEY] == "gemini-2.5-flash"


def test_decide_next_con_dau_noi_dung_ban_DU_PHONG_khi_tu_xa_hong(monkeypatch):
    """Ca đáng giá nhất của con dấu: người vận hành BẬT nhà cung cấp từ xa, nhưng bản chép này
    thực ra do Whisper cục bộ tạo ra (từ xa vừa hỏng). Con dấu phải nói sự thật đó."""
    monkeypatch.setattr(settings, "transcribe_provider", "whisper-1")
    res = _call_decide_next(monkeypatch, _result(engine="local:small"))

    assert res.json()[KEY] == "local:small", "con dấu phải theo bản chép THẬT, không theo cấu hình"


def test_decide_next_khong_co_audio_thi_con_dau_la_None(monkeypatch):
    """Nhánh `answerText` không chép lời nào ⇒ None = "không biết", KHÔNG bịa tên engine."""
    async def fake_decide(**kwargs):
        return {"action": "end", "nextQuestion": None, "reason": "r"}

    monkeypatch.setattr(main_module.provider, "decide_next", fake_decide)

    res = client.post("/api/v1/decide-next", headers=_HEADERS, json={
        "jobCategory": "FE",
        "answerText": "Tôi trả lời bằng chữ.",
        "currentQuestion": "Q",
        "criteria": _CRITERIA,
    })

    assert res.status_code == 200
    assert res.json()[KEY] is None


# ── callback chấm của worker ─────────────────────────────────────────────────────────
def test_payload_cham_mang_con_dau_engine():
    payload = worker.make_score_payload(
        "a1", "transcript", "v1", [], 1, transcript_engine="whisper-1")

    assert KEY in payload
    assert payload[KEY] == "whisper-1"


def test_payload_cham_mac_dinh_None_khong_bia_local():
    """Call site cũ không truyền ⇒ None = "không biết". Mặc định "local" sẽ nói dối về những bản
    chép do đường khác tạo ra."""
    payload = worker.make_score_payload("a1", "transcript", "v1", [], 1)

    assert payload[KEY] is None


async def test_worker_duong_tinh_dong_dau_engine_cua_lan_chep(monkeypatch):
    """Đường TĨNH: worker tự chép lời ⇒ con dấu lấy thẳng từ kết quả chép."""
    sent = await _run_worker(monkeypatch, body={
        "answerId": "a1", "audioObjectKey": "k.webm", "questionContent": "Q",
        "jobCategory": "BE", "criteria": [], "rubricVersion": "v1",
    }, transcribe_result=_result(engine="whisper-1"))

    assert sent[KEY] == "whisper-1"


async def test_worker_doc_con_dau_PascalCase_tu_queue(monkeypatch):
    """🔴 CA THẬT của dây RabbitMQ — và là ca dễ bỏ lọt nhất.

    `ScoringJobPublisher.cs` gọi `JsonSerializer.Serialize(job)` KHÔNG truyền options ⇒ dùng
    `JsonSerializerOptions.Default` = **PascalCase**, khác đường HTTP của ASP.NET Core (camelCase).
    Nên trên dây này khoá thật là `TranscriptEngine`, không phải `transcriptEngine`.

    Chỉ đọc camelCase thì con dấu chết IM LẶNG đúng ở đường republisher và đường adaptive→job:
    không lỗi, không cảnh báo, chỉ là một cột NULL — đúng hạng lỗi cả vòng này sinh ra để chặn.
    """
    sent = await _run_worker(monkeypatch, body={
        "AnswerId": "a1", "QuestionContent": "Q", "JobCategory": "BE",
        "Criteria": [], "RubricVersion": "v1",
        "Transcript": "bản chép có sẵn", "TranscriptEngine": "gemini-2.5-flash",
    })

    assert sent[KEY] == "gemini-2.5-flash"
    assert sent["transcript"] == "bản chép có sẵn"


async def test_worker_doc_con_dau_camelCase_tu_queue(monkeypatch):
    """Vế phòng thủ: nếu .NET đổi sang Web defaults thì con dấu vẫn phải đi tiếp.

    Giữ CẢ HAI theo đúng mẫu đã có sẵn trong `worker.py` cho `transcript`/`deliveryMetrics` —
    một dây mà hai bên tuần tự hoá theo hai quy ước khác nhau là chuyện đã xảy ra thật ở đây.
    """
    sent = await _run_worker(monkeypatch, body={
        "answerId": "a1", "questionContent": "Q", "jobCategory": "BE",
        "criteria": [], "rubricVersion": "v1",
        "transcript": "bản chép có sẵn", "transcriptEngine": "gemini-2.5-flash",
    })

    assert sent[KEY] == "gemini-2.5-flash"


async def test_worker_gui_HTTP_bang_camelCase_du_doc_vao_la_PascalCase(monkeypatch):
    """Hai chiều, hai quy ước — và cả hai đều phải đúng CÙNG LÚC.

    ĐỌC từ queue = chấp nhận cả hai casing · GỬI qua HTTP = camelCase (ASP.NET Core Web defaults).
    Test này khoá chiều GỬI ngay trên chính job PascalCase, nên nếu ai đó "thống nhất cho gọn"
    bằng cách echo lại đúng casing đã đọc thì callback .NET sẽ bind hụt.
    """
    sent = await _run_worker(monkeypatch, body={
        "AnswerId": "a1", "QuestionContent": "Q", "JobCategory": "BE",
        "Criteria": [], "RubricVersion": "v1",
        "Transcript": "bản chép có sẵn", "TranscriptEngine": "whisper-1",
    })

    assert "TranscriptEngine" not in sent, "callback HTTP phải camelCase, không echo PascalCase"
    assert sent[KEY] == "whisper-1"


async def test_worker_job_cu_khong_co_con_dau_thi_None(monkeypatch):
    """Job cũ / .NET chưa deploy phần này ⇒ None, KHÔNG bịa "local"."""
    sent = await _run_worker(monkeypatch, body={
        "answerId": "a1", "questionContent": "Q", "jobCategory": "BE",
        "criteria": [], "rubricVersion": "v1", "transcript": "bản chép có sẵn",
    })

    assert sent[KEY] is None


# ── khung chạy worker (mirror test_worker_dlq.py) ────────────────────────────────────
async def _run_worker(monkeypatch, *, body, transcribe_result=None):
    """Chạy `process_message` với mọi cửa I/O bị chặn; trả payload đã gửi callback."""
    import json
    from unittest.mock import MagicMock

    import aio_pika

    from app.providers.gemini import ScoreOutcome

    captured: dict = {}

    async def fake_post(payload):
        captured.update(payload)

    monkeypatch.setattr(worker, "post_callback", fake_post)
    monkeypatch.setattr(worker.s3_client, "download_fileobj", lambda *a, **k: None)
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed",
                        MagicMock(return_value=transcribe_result or _result()))
    monkeypatch.setattr(worker.provider, "score",
                        AsyncMock(return_value=ScoreOutcome(scores=[], sample_answer=None)))

    message = MagicMock(spec=aio_pika.IncomingMessage)
    message.body = json.dumps(body).encode()
    message.ack = AsyncMock()
    message.nack = AsyncMock()

    await worker.process_message(message)
    message.ack.assert_awaited_once()
    return captured
