# tests/test_score_all_criteria_bk43.py — BK43: lượt chấm phải trả ĐỦ mọi tiêu chí, ép bằng SCHEMA.
#
# Vì sao: trước BK43 `scores` là một mảng tự do, model trả thiếu một phần tử vẫn khớp schema; guard
# "thiếu tiêu chí" chỉ bắt được sau lượt gọi rồi đánh hỏng CẢ câu trả lời (`Failed`). Log worker prod
# (3 ngày tới 2026-10-05): 5 câu dính, 7 lần thiếu, 5/7 là tiêu chí CÁCH NÓI luôn chấm — và lỗi ngẫu
# nhiên theo thời điểm nên vòng thử lại AI3 không cứu được. Nay mỗi tiêu chí là một khoá BẮT BUỘC.
#
# Test chặn ở `_generate` (mẫu test_score_thinking_budget.py): soi config `score()` dựng ra và cách nó
# đọc output. KHÔNG gọi Gemini thật — phép đo với Gemini thật nằm ở progress.md (BK43).
import json
from types import SimpleNamespace

import pytest
from google.genai import types

from app.providers.gemini import GeminiProvider, _score_items, _score_response_schema

C1 = "24659ea8-5be3-4dbb-9213-80bd91d230d0"
C2 = "73487bd1-8bc4-4596-b2ed-07f62223a0f7"
C3 = "6c995a56-6b23-40df-90b1-0d93b00ab588"

_LEVELS = [{"score": s, "descriptor": f"Mức {s}"} for s in range(0, 6)]


def _crit(cid: str, name: str) -> dict:
    return {"criterionId": cid, "name": name, "description": "", "maxScore": 5,
            "weight": 0.2, "levels": _LEVELS}


CRITERIA = [_crit(C1, "Chiều sâu kỹ thuật"), _crit(C2, "Thuật ngữ chuyên ngành"),
            _crit(C3, "Ngữ pháp & dùng từ")]


def _entry(level: int, reasoning: str = 'Ứng viên nói "dùng cơ sở dữ liệu riêng".') -> dict:
    return {"score": level, "levelMatched": level, "reasoning": reasoning}


def _fake(monkeypatch, provider, payload):
    """Thay `_generate` bằng bản ghi lại config rồi trả `payload` (đã json hoá)."""
    captured: dict = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["config"] = config
        return SimpleNamespace(text=json.dumps(payload), usage_metadata=None)

    monkeypatch.setattr(provider, "_generate", fake_generate)
    return captured


# ── Schema: ép đủ khoá bằng cấu trúc ──────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_schema_moi_tieu_chi_la_mot_khoa_bat_buoc_dung_thu_tu_rubric(monkeypatch):
    """Đây là toàn bộ bản sửa: thiếu `required` (hay thiếu một id trong đó) là model lại được phép
    trả thiếu y như mảng cũ — và triệu chứng chỉ quay lại dưới dạng `Failed` ngẫu nhiên trên prod."""
    provider = GeminiProvider()
    payload = {"scores": {C1: _entry(2), C2: _entry(3), C3: _entry(4)}, "sampleAnswer": "Mẫu."}
    captured = _fake(monkeypatch, provider, payload)

    await provider.score("Q?", "câu trả lời", "BE", CRITERIA)

    scores = captured["config"].response_schema["properties"]["scores"]
    assert scores["type"] == "object"
    assert list(scores["properties"]) == [C1, C2, C3]
    assert scores["required"] == [C1, C2, C3]
    # Giữ thứ tự rubric như dạng mảng cũ — đổi hình dạng output không được kéo theo đổi thứ tự viết.
    assert scores["propertyOrdering"] == [C1, C2, C3]
    for cid in (C1, C2, C3):
        assert scores["properties"][cid]["required"] == ["score", "levelMatched", "reasoning"]


@pytest.mark.asyncio
async def test_job_rabbitmq_pascalcase_van_ra_dung_id(monkeypatch):
    """Job chấm thật đi qua RabbitMQ là PascalCase (`CriterionId`). Khoá schema dựng từ chữ thường
    thì mọi khoá thành "None" ⇒ model trả đúng khoá đó ⇒ guard bỏ hết ⇒ `Failed` 100%."""
    provider = GeminiProvider()
    pascal = [{"CriterionId": C1, "Name": "A", "MaxScore": 5, "Levels": [{"Score": 0}, {"Score": 5}]},
              {"CriterionId": C2, "Name": "B", "MaxScore": 5, "Levels": [{"Score": 0}, {"Score": 5}]}]
    payload = {"scores": {C1: _entry(5), C2: _entry(0)}, "sampleAnswer": "Mẫu."}
    captured = _fake(monkeypatch, provider, payload)

    out = await provider.score("Q?", "câu trả lời", "BE", pascal)

    assert captured["config"].response_schema["properties"]["scores"]["required"] == [C1, C2]
    assert [s["criterionId"] for s in out.scores] == [C1, C2]


def test_schema_duoc_sdk_chap_nhan_ca_khoa_uuid_va_property_ordering():
    """SDK dựng `types.Schema` từ dict lúc gửi request. Nếu nó không nhận khoá UUID hay
    `propertyOrdering` thì lỗi nổ ở ĐƯỜNG GỌI THẬT (400) — chỗ mà mọi test chặn `_generate` khác
    đều không với tới."""
    schema = types.Schema.model_validate(
        {"type": "object", "properties": {"scores": _score_response_schema([C1, C2])},
         "required": ["scores"]})
    scores = schema.properties["scores"]
    assert scores.required == [C1, C2]
    assert scores.property_ordering == [C1, C2]
    assert list(scores.properties) == [C1, C2]


def test_danh_sach_rong_giu_dang_mang_khong_de_object_rong():
    """Object không có thuộc tính nào có thể bị Gemini từ chối bằng 400 — lỗi API, worker coi là
    tạm thời và đẩy lại mãi. Giữ mảng để ca (vốn không với tới được) này hỏng như trước."""
    schema = _score_response_schema([])
    assert schema["type"] == "array"
    assert schema["items"]["required"] == ["criterionId", "score", "levelMatched", "reasoning"]


# ── Đọc output: object mới + mảng cũ ─────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_doc_dang_object_tra_du_tieu_chi_va_muc(monkeypatch):
    provider = GeminiProvider()
    payload = {"scores": {C1: _entry(2), C2: _entry(3), C3: _entry(4)}, "sampleAnswer": "Mẫu."}
    _fake(monkeypatch, provider, payload)

    out = await provider.score("Q?", "câu trả lời", "BE", CRITERIA)

    assert {s["criterionId"]: s["levelMatched"] for s in out.scores} == {C1: 2, C2: 3, C3: 4}
    assert all(s["score"] == float(s["levelMatched"]) for s in out.scores)
    assert out.sample_answer == "Mẫu."


@pytest.mark.asyncio
async def test_khoa_la_nguon_su_that_cua_id(monkeypatch):
    """Schema ràng buộc KHOÁ, không ràng buộc chuỗi bên trong. Lấy id bên trong thì một model nhét
    nhầm id (vd chép id của tiêu chí kế bên) sẽ làm tiêu chí thật biến mất ⇒ `Failed` trở lại."""
    provider = GeminiProvider()
    wrong_inside = {**_entry(1), "criterionId": C2}
    payload = {"scores": {C1: wrong_inside, C2: _entry(3), C3: _entry(4)}, "sampleAnswer": "Mẫu."}
    _fake(monkeypatch, provider, payload)

    out = await provider.score("Q?", "câu trả lời", "BE", CRITERIA)

    assert {s["criterionId"]: s["levelMatched"] for s in out.scores} == {C1: 1, C2: 3, C3: 4}


@pytest.mark.asyncio
async def test_model_lo_schema_tra_mang_cu_van_doc_duoc(monkeypatch):
    """Model đôi khi lờ `response_schema` (tiền lệ `_question_text`). Trả mảng ĐỦ tiêu chí thì đó
    là một lượt chấm đúng — bỏ nó đi là tự tạo ra đúng thứ BK43 sinh ra để chặn."""
    provider = GeminiProvider()
    payload = {"scores": [{"criterionId": C1, **_entry(2)}, {"criterionId": C2, **_entry(3)},
                          {"criterionId": C3, **_entry(4)}],
               "sampleAnswer": "Mẫu."}
    _fake(monkeypatch, provider, payload)

    out = await provider.score("Q?", "câu trả lời", "BE", CRITERIA)

    assert {s["criterionId"]: s["levelMatched"] for s in out.scores} == {C1: 2, C2: 3, C3: 4}


@pytest.mark.asyncio
async def test_van_thieu_tieu_chi_thi_guard_int9_van_bao_loi(monkeypatch):
    """Lưới cuối giữ nguyên: lượt nào lọt thiếu (model lờ schema) vẫn phải là `ValueError` để
    worker thử lại — KHÔNG được âm thầm gửi điểm trên bộ tiêu chí hụt xuống .NET."""
    provider = GeminiProvider()
    payload = {"scores": {C1: _entry(2), C3: _entry(4), "khoa-bia": _entry(5)},
               "sampleAnswer": "Mẫu."}
    _fake(monkeypatch, provider, payload)

    with pytest.raises(ValueError, match="thiếu tiêu chí"):
        await provider.score("Q?", "câu trả lời", "BE", CRITERIA)


def test_score_items_bo_phan_tu_khong_phai_object():
    assert _score_items({C1: _entry(1), C2: "rác"}) == [{**_entry(1), "criterionId": C1}]
    assert _score_items([{"criterionId": C1}, "rác", 3]) == [{"criterionId": C1}]
    assert _score_items(None) == []
    assert _score_items("rác") == []
