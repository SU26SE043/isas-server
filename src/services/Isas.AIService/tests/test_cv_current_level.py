# tests/test_cv_current_level.py — `currentLevel`: trình độ HIỆN TẠI suy từ CV.
#
# Khác `level` (= trình độ MỤC TIÊU người dùng chọn ở wizard). Dùng làm SÀN cho prompt roadmap:
# bỏ phần nhập môn người học đã nắm. `None` = CV không đủ căn cứ — đo được 87% CV thật không ghi
# trình độ ở đâu, nên "không biết" là câu trả lời hạng nhất chứ không phải fallback.
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

def test_wire_hai_dau_deu_khai_current_level():
    """Thiếu khai ở BẤT KỲ đầu nào là hỏng IM LẶNG: .NET gửi, pydantic nuốt, không lỗi nào nổ.

    Tiền lệ: `focusCriteria` (BC14/F2b) · `metricsVersion` · `adaptiveMaxQuestions` · `grounding`.
    """
    assert "currentLevel" in AnalyzeCvResponse.model_fields, \
        "AIService không khai ⇒ field bị xoá khỏi response, .NET không bao giờ thấy"
    assert "currentLevel" in GenerateRoadmapRequest.model_fields, \
        "AIService không khai ⇒ .NET gửi mà prompt roadmap không bao giờ nhận"


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
# `current_level`/`mode` vẫn là tham số HỢP LỆ của `build_roadmap_prompt` (giữ tương thích chữ
# ký; `mode` còn chỉnh câu dẫn — xem `test_roadmap_mode.py`) nhưng KHÔNG còn tự render khối nội
# dung nào trong hàm này. §(1)-(3)-(6) ở trên/dưới KHÔNG đụng tới `build_roadmap_prompt` nên vẫn
# giữ nguyên giá trị (hợp đồng dây `currentLevel`, prompt phân tích CV, guard provider).


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
