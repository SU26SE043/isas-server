# tests/test_cv_current_level.py — `currentLevel`: trình độ HIỆN TẠI suy từ CV.
#
# Khác `level` (= trình độ MỤC TIÊU người dùng chọn ở wizard). `None` = CV không đủ căn cứ — đo
# được 87% CV thật không ghi trình độ ở đâu, nên "không biết" là câu trả lời hạng nhất chứ không
# phải fallback.
#
# 🔴 REC1-B7 — TIỀN ĐỀ ĐÃ ĐẢO: `currentLevel` KHÔNG CÒN "dùng làm SÀN cho prompt roadmap" như
# comment gốc phía trên nói. `GenerateRoadmapRequest` (schemas.py) đã GỠ HẲN field này —
# `currentLevel` nay CHỈ còn sống trong `AnalyzeCvResponse` (kết quả `/analyze-cv`, lưu vào
# `cv_analyses.current_level` phía .NET) và KHÔNG BAO GIỜ chảy tiếp sang roadmap nữa. Mục (1)-(6)
# CÒN LẠI của file này (guard chuẩn hoá giá trị, prompt phân tích CV hỏi đúng thứ) đo đúng phần
# CÒN SỐNG của cơ chế — không đổi.
import inspect
import json
from unittest.mock import AsyncMock

import pytest

from app.prompts import build_roadmap_prompt, build_cv_analysis_prompt
from app.providers.gemini import GeminiProvider
from app.schemas import AnalyzeCvResponse, CV_CURRENT_LEVELS, GenerateRoadmapRequest
from app.seniority import LEVELS


def _resp(payload: dict):
    r = AsyncMock()
    r.text = json.dumps(payload)
    return r


_BASE = {"summary": "Tóm tắt", "strengths": [], "weaknesses": [], "suggestions": []}


# ── (1) HỢP ĐỒNG DÂY — lớp bug `extra='ignore'` đã cắn repo 4 lần ─────────────────────────────

def test_wire_analyze_cv_response_khai_current_level():
    """Thiếu khai là hỏng IM LẶNG: .NET đọc, pydantic không trả field, không lỗi nào nổ.

    Tiền lệ: `focusCriteria` (BC14/F2b) · `metricsVersion` · `adaptiveMaxQuestions` · `grounding`.
    """
    assert "currentLevel" in AnalyzeCvResponse.model_fields, \
        "AIService không khai ⇒ field bị xoá khỏi response, .NET không bao giờ thấy"


def test_wire_generate_roadmap_request_KHONG_CON_khai_current_level():
    """🔴 REC1-B7 — ĐẢO NGƯỢC test gốc (từng khẳng định `GenerateRoadmapRequest` PHẢI khai
    `currentLevel`, cùng lý do `focusCriteria`/`metricsVersion`). Field này đã GỠ HẲN khỏi
    `GenerateRoadmapRequest`: dù .NET có lỡ gửi lại, `extra='ignore'` (mặc định pydantic không
    `model_config`) sẽ nuốt câm — CỐ Ý, vì phía .NET cũng đã gỡ hẳn field này khỏi payload rồi
    (xem `AiServiceRoadmapGenerator.cs`), nên đây không còn là lớp bug "quên khai" nữa."""
    assert "currentLevel" not in GenerateRoadmapRequest.model_fields


def test_tap_gia_tri_khong_troi_khoi_seniority_levels():
    """`CV_CURRENT_LEVELS` khai lại trong `schemas.py` để giữ module đó phụ thuộc mỗi pydantic.

    Đánh đổi là nó có thể trôi khỏi `app.seniority.LEVELS`. Test này là thứ duy nhất chặn.
    """
    assert CV_CURRENT_LEVELS == LEVELS


def test_cv_text_khong_con_la_tham_so_cua_roadmap_prompt():
    """CV thô đã bị gỡ khỏi luồng roadmap — xem lý do ở `build_roadmap_prompt` docstring."""
    assert "cv_text" not in inspect.signature(build_roadmap_prompt).parameters


# ── (2) PROMPT PHÂN TÍCH CV — hỏi đúng thứ, và nói rõ được phép không biết ────────────────────

def test_prompt_phan_tich_cv_hoi_current_level_va_cho_phep_null():
    for requirements in (None, [{"requirementId": "r1", "text": "3 năm BE", "priority": "MustHave"}]):
        p = build_cv_analysis_prompt("CV", None, "BE", requirements=requirements)
        assert "currentLevel" in p, "phải hỏi ở CẢ nhánh thường lẫn nhánh requirement"
        assert "Fresher / Junior / Middle / Senior" in p
        assert "null" in p, "phải nói rõ được phép trả null khi không đủ căn cứ"


# ── (3) GUARD PROVIDER — không đoán, và không phá tập khoá của dict trả về ────────────────────

@pytest.mark.asyncio
@pytest.mark.parametrize("model_tra, mong_doi", [
    ("Senior", "Senior"),
    ("senior", "Senior"),      # model sinh chữ thường ⇒ chuẩn hoá, KHÔNG vứt
    ("  middle  ", "Middle"),
    ("Lead", None),            # ngoài tập ⇒ không biết
    ("", None),
    (None, None),
])
async def test_guard_current_level(monkeypatch, model_tra, mong_doi):
    provider = GeminiProvider()
    payload = dict(_BASE)
    if model_tra is not None:
        payload["currentLevel"] = model_tra
    monkeypatch.setattr(provider, "_generate", AsyncMock(return_value=_resp(payload)))

    result = await provider.analyze_cv("cv", None, "BE")
    assert result.get("currentLevel") == mong_doi


@pytest.mark.asyncio
async def test_khong_hop_le_thi_KHONG_gan_khoa(monkeypatch):
    """Gắn khoá vô điều kiện (kể cả `None`) sẽ làm đỏ test khoá TẬP KHOÁ CHÍNH XÁC của dict —
    `test_cv_screening_c14.py::test_provider_b2c_analyze_cv_giu_nguyen_shape`."""
    provider = GeminiProvider()
    monkeypatch.setattr(provider, "_generate",
                        AsyncMock(return_value=_resp({**_BASE, "currentLevel": "Lead"})))
    result = await provider.analyze_cv("cv", None, "BE")
    assert "currentLevel" not in result


@pytest.mark.asyncio
async def test_KHONG_fallback_ve_junior(monkeypatch):
    """`app.seniority.normalize` fail-open về "Junior" — CỐ Ý không dùng nó ở đây.

    Fallback "Junior" biến "CV không đủ căn cứ" thành một mức trông-như-đã-xác-định, rồi mức đó
    đi thẳng vào prompt roadmap làm sàn. Người học sẽ bị bỏ qua đúng phần nền họ đang cần.
    """
    provider = GeminiProvider()
    monkeypatch.setattr(provider, "_generate",
                        AsyncMock(return_value=_resp({**_BASE, "currentLevel": "Chuyên gia"})))
    result = await provider.analyze_cv("cv", None, "BE")
    assert result.get("currentLevel") != "Junior"
    assert result.get("currentLevel") is None


# 🔴 MIS1-B2 — §(4) "KHỐI SÀN TRONG PROMPT ROADMAP" VÀ §(5) "LEVELUP TRỘN ĐÔI" đã bị XOÁ khỏi
# đây. Cả hai kiểm nội dung của hai khối MIS1-B2 gỡ HẲN khỏi `build_roadmap_prompt`:
#   - khối SÀN "TRÌNH ĐỘ HIỆN TẠI CỦA NGƯỜI HỌC ... KHÔNG sinh chặng/bài nhập môn thuộc mức X" —
#     vô nghĩa khi nội dung roadmap nay gom từ LỖI THẬT (mistakes), không còn suy từ CV.
#   - `roadmap_mode_block` nhánh LevelUp+điểm yếu ("PHÂN BỔ LỘ TRÌNH" — nửa sau nâng lên mục
#     tiêu) VÀ nhánh Reinforce ("CHẾ ĐỘ ÔN TẬP (REINFORCE)") — cả hai mâu thuẫn thẳng với luật
#     gom chủ đề TỪ LỖI (mọi milestone phải rút ra từ lỗi thật, không phải nửa-tự-do/giữ-nguyên).
#
# 🔴 REC1-B7 đi thêm một bước so với ghi chú gốc phía trên: `current_level` KHÔNG còn là "tham số
# hợp lệ giữ tương thích chữ ký" nữa — nó đã GỠ HẲN khỏi chữ ký `build_roadmap_prompt` (chỉ `mode`
# còn giữ, vẫn chỉnh câu dẫn — xem `test_roadmap_mode.py`). §(1)-(3)-(6) ở trên/dưới KHÔNG đụng
# tới `build_roadmap_prompt` nên vẫn giữ nguyên giá trị (prompt phân tích CV, guard provider) —
# CHỈ §(1) đổi (xem `test_wire_generate_roadmap_request_KHONG_CON_khai_current_level` ở trên).


# ── (6) HỒI QUY: `grounding: null` KHÔNG được làm hỏng phân tích CV ──────────────────────────

def test_analyze_cv_request_nhan_grounding_null():
    """🔴 Bug production sống 4 ngày (18/08 → 22/08), tìm ra khi chạy L3 cho `currentLevel`.

    `AiServiceCvAnalyzer.AnalyzeAsync` để `grounding` mặc định `null` ở đường phân tích CV
    THƯỜNG (không requirement), và `JsonContent.Create` KHÔNG bỏ khoá null ⇒ payload luôn có
    `"grounding": null`. Schema khai `list[GroundingChunk] = []` (non-nullable) nên pydantic trả
    **422**, InterviewService map thành **502**, và người dùng chỉ thấy "AIService gặp lỗi".

    Đo được: 26 lượt legacy-mode trên production, lượt cuối **17/08** — đúng một ngày trước hai
    thay đổi ngày 18/08 dựng nên cái bẫy. Nhánh requirement không dính vì nó luôn truyền list.
    """
    from app.schemas import AnalyzeCvRequest

    r = AnalyzeCvRequest.model_validate(
        {"cvText": "CV", "jobCategory": "BE", "grounding": None})
    assert r.grounding is None

    # vắng khoá cũng phải chạy (đường cũ)
    assert AnalyzeCvRequest.model_validate({"cvText": "CV"}).grounding is None
    # có list thì giữ nguyên
    r3 = AnalyzeCvRequest.model_validate(
        {"cvText": "CV", "grounding": [{"chunkId": "c1", "content": "x"}]})
    assert r3.grounding is not None and len(r3.grounding) == 1
