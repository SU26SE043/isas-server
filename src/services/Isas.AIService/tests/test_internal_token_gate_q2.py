# tests/test_internal_token_gate_q2.py — Q2/GEN-7: gate X-Internal-Token cho endpoint SINH.
#
# LỖ ĐÃ TÁI HIỆN TRÊN DEPLOY: một POST ẩn danh (không JWT, không X-Internal-Token) tới
# /api/v1/ai/generate-questions qua gateway trả 200 + câu hỏi sinh thật, và `ai_usage_logs` ghi
# ngay một dòng ~635 token / $0.0013 vào tài khoản Gemini của dự án. Không rate-limit nào phủ
# nhóm này (limiter chỉ gắn /campaign/public/*). Trước bản vá, chỉ 5/13 endpoint gọi
# `_valid_internal_token`; 8 endpoint dưới đây để trần.
#
# ⚠ CÁCH ĐO — đây là chỗ dễ tự lừa mình nhất:
# body RỖNG trả **422** vì FastAPI validate pydantic TRƯỚC khi vào handler. 422 KHÔNG chứng minh
# gate hoạt động (endpoint chưa gate cũng trả 422 y hệt). Nên mọi ca 401 dưới đây gửi **body hợp
# lệ đúng schema** — nếu gate bị gỡ, cùng request đó chạy tới provider và trả 200/502, không
# phải 422. Test `test_body_hop_le_khong_bi_422` khoá luôn tiền đề đó lại.
import io

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.config import settings

client = TestClient(main_module.app)

_HEADERS = {"X-Internal-Token": settings.internal_token}


# Body HỢP LỆ theo app/schemas.py cho từng endpoint (xem cảnh báo 422 ở đầu file).
_VALID_BODY = {
    "/api/v1/generate-questions":     {"jobCategory": "BA", "language": "vi", "count": 1},
    "/api/v1/suggest-criteria":       {"jobCategory": "BE", "jdText": "JD", "count": 4},
    "/api/v1/suggest-criterion-levels": {"jobCategory": "BE", "criteria": [
        {"criterionId": "c1", "name": "Chiều sâu kỹ thuật", "maxScore": 5}]},
    # Đắt nhất nhóm: 1 lượt sinh 3 bài + 3 lượt chấm = 4 lời gọi Gemini cho MỘT request.
    "/api/v1/score-preview": {"jobCategory": "BE", "question": "Câu hỏi?", "criteria": [
        {"criterionId": "c1", "name": "Chiều sâu kỹ thuật", "maxScore": 5, "weight": 1.0,
         "levels": [{"score": 0, "descriptor": "d0"}, {"score": 5, "descriptor": "d5"}],
         "expectedWeak": 0, "expectedGood": 0, "expectedExcellent": 5}]},
    "/api/v1/analyze-cv":             {"cvText": "kinh nghiệm 3 năm Python"},
    "/api/v1/generate-roadmap":       {"jobCategory": "BE", "level": "Junior"},
    "/api/v1/generate-lesson-theory": {"jobCategory": "BE", "level": "Junior",
                                       "lessonTitle": "Chuẩn hoá DB",
                                       "focusCriteria": ["Thiết kế CSDL"]},
    "/api/v1/summarize-roadmap":      {"jobCategory": "BE", "level": "Junior",
                                       "criteriaProgress": []},
    "/api/v1/summarize-session":      {"jobCategory": "BE", "overallScore": 62.5,
                                       "criteriaScores": []},
}

# /transcribe là multipart, không phải JSON → tách riêng.
_TRANSCRIBE = "/api/v1/transcribe"
GATED_JSON = sorted(_VALID_BODY)
ALL_GATED = GATED_JSON + [_TRANSCRIBE]


def _post(path, headers):
    if path == _TRANSCRIBE:
        return client.post(path, headers=headers,
                           files={"file": ("a.webm", io.BytesIO(b"fake-audio"), "audio/webm")})
    return client.post(path, headers=headers, json=_VALID_BODY[path])


@pytest.fixture
def provider_no_touch(monkeypatch):
    """Mọi đường tốn tiền đều NỔ nếu bị chạm.

    Đây mới là điều cần chứng minh: không phải "trả 401", mà là "từ chối TRƯỚC KHI đốt token
    Gemini / chạy Whisper". Chỉ assert status code thì một bản vá gọi provider rồi mới 401 vẫn
    xanh — mà đúng cái tốn tiền lại nằm ở lời gọi đó.
    """
    def boom(*args, **kwargs):
        raise AssertionError("Gate hở: đã chạm provider/transcriber trước khi từ chối request.")

    for name in ("generate", "suggest_criteria", "suggest_criterion_levels", "analyze_cv",
                 "generate_roadmap", "generate_preview_answers", "score",
                 "generate_lesson_theory", "summarize_roadmap", "summarize_session"):
        monkeypatch.setattr(main_module.provider, name, boom)
    monkeypatch.setattr(main_module.transcriber, "transcribe_detailed", boom)


# ── 401: thiếu token / sai token ────────────────────────────────────────────
@pytest.mark.parametrize("path", ALL_GATED)
def test_thieu_internal_token_tra_401(path, provider_no_touch):
    assert _post(path, headers={}).status_code == 401


@pytest.mark.parametrize("path", ALL_GATED)
def test_sai_internal_token_tra_401(path, provider_no_touch):
    assert _post(path, headers={"X-Internal-Token": "sai-token"}).status_code == 401


@pytest.mark.parametrize("path", ALL_GATED)
def test_token_rong_tra_401(path, provider_no_touch):
    """Header có mặt nhưng rỗng → vẫn 401 (fail-closed).

    Không phải ca lý thuyết: client .NET dùng `TryAddWithoutValidation(name, _token)` với
    `_token` đọc từ config — thiếu `Internal:Token` thì header đi ra RỖNG chứ không vắng mặt.
    """
    assert _post(path, headers={"X-Internal-Token": ""}).status_code == 401


# ── 200: token đúng thì vẫn đi lọt (gate không chặn nhầm luồng thật) ────────
def test_generate_questions_dung_token_tra_200(monkeypatch):
    from app.providers.gemini import QuestionGenerationResult

    async def fake(*a, **k):
        return QuestionGenerationResult(questions=["Q1"], citations=None)

    monkeypatch.setattr(main_module.provider, "generate", fake)
    res = _post("/api/v1/generate-questions", _HEADERS)
    assert res.status_code == 200
    assert res.json() == {"questions": ["Q1"]}


def test_suggest_criteria_dung_token_tra_200(monkeypatch):
    """Trước Q2 endpoint này KHÔNG có test HTTP nào — nó chỉ được gọi từ Campaign."""
    async def fake(*a, **k):
        return [{"name": "Kỹ thuật", "description": None, "weight": 1.0, "maxScore": 5}]

    monkeypatch.setattr(main_module.provider, "suggest_criteria", fake)
    res = _post("/api/v1/suggest-criteria", _HEADERS)
    assert res.status_code == 200
    assert res.json()["criteria"][0]["name"] == "Kỹ thuật"


def test_analyze_cv_dung_token_tra_200(monkeypatch):
    async def fake(*a, **k):
        return {"summary": "s", "strengths": ["A"], "weaknesses": [], "suggestions": []}

    monkeypatch.setattr(main_module.provider, "analyze_cv", fake)
    assert _post("/api/v1/analyze-cv", _HEADERS).status_code == 200


def test_generate_roadmap_dung_token_tra_200(monkeypatch):
    async def fake(*a, **k):
        return [{"title": "M1", "focusCriteria": ["A"], "lessons": [{"title": "L1"}]}]

    monkeypatch.setattr(main_module.provider, "generate_roadmap", fake)
    assert _post("/api/v1/generate-roadmap", _HEADERS).status_code == 200


def test_generate_lesson_theory_dung_token_tra_200(monkeypatch):
    async def fake(*a, **k):
        return ("# Bài\n\nNội dung", [], None, None)

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)
    assert _post("/api/v1/generate-lesson-theory", _HEADERS).status_code == 200


def test_summarize_roadmap_dung_token_tra_200(monkeypatch):
    async def fake(*a, **k):
        return {"strengths": [], "weaknesses": [], "improvements": [], "overallComment": "ok"}

    monkeypatch.setattr(main_module.provider, "summarize_roadmap", fake)
    assert _post("/api/v1/summarize-roadmap", _HEADERS).status_code == 200


def test_summarize_session_dung_token_tra_200(monkeypatch):
    async def fake(*a, **k):
        return {"overallComment": "nhận xét"}

    monkeypatch.setattr(main_module.provider, "summarize_session", fake)
    assert _post("/api/v1/summarize-session", _HEADERS).status_code == 200


def test_transcribe_dung_token_tra_200(monkeypatch):
    """Trước Q2 endpoint này cũng KHÔNG có test HTTP nào (chỉ scripts/loadtest gọi tới)."""
    from app.transcriber import TranscriptionResult

    monkeypatch.setattr(main_module.transcriber, "transcribe_detailed",
                        lambda path, lang="vi": TranscriptionResult(
                            text="xin chào", metrics=None, engine="local:small"))
    res = _post(_TRANSCRIBE, _HEADERS)
    assert res.status_code == 200
    assert res.json()["text"] == "xin chào"


# ── Bẫy đo lường: body hợp lệ phải KHÔNG ra 422 ─────────────────────────────
@pytest.mark.parametrize("path", GATED_JSON)
def test_body_hop_le_khong_bi_422(path, provider_no_touch):
    """Khoá tiền đề của mọi ca 401 ở trên.

    Nếu một body trong `_VALID_BODY` lệch schema, ca 401 tương ứng sẽ "xanh" vì lý do SAI (422
    của pydantic thay vì 401 của gate) — và bài test sẽ không còn chứng minh được gì. Ở đây gửi
    token ĐÚNG với provider đã bị vô hiệu: gate cho qua → chạm provider → 502, chứ tuyệt đối
    không được là 422.
    """
    assert _post(path, _HEADERS).status_code == 502


# ── Fail-closed khi SERVER chưa cấu hình token ─────────────────────────────
@pytest.mark.parametrize("path", ALL_GATED)
def test_server_chua_cau_hinh_token_van_401(path, provider_no_touch, monkeypatch):
    """`internal_token` rỗng phía server → TỪ CHỐI, không phải "cho qua vì chưa bật".

    Nhánh `if not expected` của `_valid_internal_token` là nửa thứ hai của lời hứa fail-closed,
    nhưng trước test này nó KHÔNG được phủ ở đâu trong repo (mọi test đều chạy với
    `internal_token="change-me"`) — kể cả cho 5 endpoint đã gate từ trước. Mutation battery Q2
    xác nhận: đổi thành `if not expected: return True` chạy qua 443/443 test mà không đỏ một cái.

    Đây đúng là hình dạng sự cố đắt nhất có thể xảy ra: deploy quên đặt INTERNAL_TOKEN, ai đó
    "sửa cho tiện" theo hướng nới, và toàn bộ 13 endpoint mở lại ra Internet trong im lặng.
    """
    monkeypatch.setattr(main_module.settings, "internal_token", "")
    # Giá trị header phải ASCII: httpx encode latin-1 và sẽ ném UnicodeEncodeError TRƯỚC khi
    # request tới app — một "đỏ" như thế không nói gì về gate cả (cùng họ với quy ước
    # "assert chuỗi vào JSON serialize phải dùng sentinel ASCII" của F17).
    assert _post(path, headers={"X-Internal-Token": "any-token"}).status_code == 401


def test_health_van_mo(provider_no_touch):
    """/health là endpoint DUY NHẤT không gate — compose healthcheck + gateway probe dựa vào nó."""
    assert client.get("/api/v1/health").status_code == 200
