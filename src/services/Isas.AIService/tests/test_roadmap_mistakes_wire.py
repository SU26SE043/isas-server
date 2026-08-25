# tests/test_roadmap_mistakes_wire.py — MIS1-B1: hợp đồng dây cho LỖI SAI trích từ buổi luyện
# đã chấm, gom vào roadmap/bài học. CHƯA đụng nội dung prompt (đó là MIS1-B2) — mọi test ở đây
# chỉ chứng minh dữ liệu ĐI QUA ĐƯỢC (schema → main.py → provider → builder), và một GOLDEN test
# chứng minh builder CHƯA dùng nó (mẫu tests/test_lesson_context_wire.py:62-68).
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
import app.providers.gemini as gemini_module
from app.config import settings
from app.prompts import build_roadmap_prompt, build_lesson_theory_prompt
from app.providers.gemini import GeminiProvider, LessonTheoryResult
from app.schemas import (
    GenerateRoadmapRequest, GenerateLessonTheoryRequest, LessonContextDto,
    RoadmapMistake, MistakeReviewItem,
)

client = TestClient(main_module.app)
_HEADERS = {"X-Internal-Token": settings.internal_token}

_MISTAKE_JSON = {
    "id": "m1",
    "criterionName": "Thiết kế CSDL",
    "scorePct": 40,
    "question": "Chuẩn hoá dữ liệu để làm gì?",
    "answer": "Em không rõ lắm...",
    "reasoning": "Chưa nêu được lý do tránh dị thường dữ liệu.",
    "sampleAnswer": "Chuẩn hoá giúp tránh dị thường khi thêm/sửa/xoá.",
}


def _fake_gemini_response(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


# ══════════════════ (1) HỢP ĐỒNG DÂY — pydantic không được nuốt ══════════════════
# 🔴 Thiếu khai = .NET gửi, HTTP 200, không lỗi không log, tính năng chết im lặng. Đúng lớp bug
# đã cắn repo nhiều lần (`focusCriteria`/BC14 · `metricsVersion` · `seniority`/SEN1 ·
# `lessonContext`).

def test_schema_khai_mistakes_o_ca_ba_noi():
    assert "mistakes" in GenerateRoadmapRequest.model_fields
    assert "mistakes" in GenerateLessonTheoryRequest.model_fields
    assert "mistakes" in LessonContextDto.model_fields


def test_roadmap_mistake_field_set():
    """Khoá CẢ TẬP TÊN — đọc từ CLASS, không phải instance (pydantic 2.13.4 cảnh báo Deprecated
    khi đọc `model_fields` qua instance; đọc qua class không dính cảnh báo đó)."""
    assert set(RoadmapMistake.model_fields) == {
        "id", "criterionName", "scorePct", "question", "answer", "reasoning", "sampleAnswer",
    }


def test_mistake_review_item_field_set():
    assert set(MistakeReviewItem.model_fields) == {"mistakeId", "whatWentWrong", "howToFixIt"}


def test_lesson_theory_result_co_bon_truong():
    """Khoá việc mở rộng NamedTuple ở app/providers/gemini.py — thiếu `mistake_review` thì mọi
    unpack 3-trường cũ vẫn "đúng", nhưng call site 4-trường mới sẽ vỡ."""
    assert len(LessonTheoryResult._fields) == 4
    assert LessonTheoryResult._fields == ("theory", "resources", "cited_chunk_ids",
                                          "mistake_review")


# ══════════════════ (2) DỰNG TỪ JSON THÔ — đúng như .NET gửi ══════════════════

def test_generate_roadmap_request_nhan_mistakes_tu_json_tho():
    req = GenerateRoadmapRequest.model_validate({
        "jobCategory": "BE", "level": "Junior",
        "mistakes": [_MISTAKE_JSON],
    })
    assert req.mistakes == [RoadmapMistake(**_MISTAKE_JSON)]


def test_generate_lesson_theory_request_nhan_mistakes_tu_json_tho():
    """Đường lesson-theory là đường DUY NHẤT nhận `answer`/`sampleAnswer` — cả hai phải sống sót
    qua model_validate, không bị lọc rớt."""
    req = GenerateLessonTheoryRequest.model_validate({
        "jobCategory": "BE", "level": "Junior", "lessonTitle": "Chuẩn hoá DB",
        "focusCriteria": ["Thiết kế CSDL"],
        "mistakes": [_MISTAKE_JSON],
    })
    assert req.mistakes == [RoadmapMistake(**_MISTAKE_JSON)]
    assert req.mistakes[0].answer == _MISTAKE_JSON["answer"]
    assert req.mistakes[0].sampleAnswer == _MISTAKE_JSON["sampleAnswer"]


def test_lesson_context_dto_nhan_mistakes_tu_json_tho():
    dto = LessonContextDto.model_validate({
        "title": "Chuẩn hoá DB", "mistakes": [_MISTAKE_JSON],
    })
    assert dto.mistakes == [RoadmapMistake(**_MISTAKE_JSON)]


def test_caller_cu_khong_gui_mistakes_thi_none():
    """Vắng ⇒ None (KHÔNG phải []) — hành vi trước MIS1 giữ nguyên cho mọi caller cũ."""
    assert GenerateRoadmapRequest(jobCategory="BE", level="Junior").mistakes is None
    assert GenerateLessonTheoryRequest(
        jobCategory="BE", level="Junior", lessonTitle="T", focusCriteria=[]).mistakes is None
    assert LessonContextDto(title="T").mistakes is None


def test_roadmap_mistake_field_toi_gian_van_hop_le():
    """Chỉ `id`/`criterionName`/`reasoning` bắt buộc — 4 field còn lại optional-safe."""
    m = RoadmapMistake(id="m1", criterionName="SQL", reasoning="sai vì X")
    assert m.scorePct is None and m.question is None
    assert m.answer is None and m.sampleAnswer is None


# ══════════════════ (3) RESPONSE — mistakeIds / mistakeReview ══════════════════

def test_roadmap_milestone_lesson_mac_dinh_mistake_ids_rong():
    from app.schemas import RoadmapLesson, RoadmapMilestone

    lesson = RoadmapLesson(title="L1")
    milestone = RoadmapMilestone(title="M1", focusCriteria=[], lessons=[lesson])
    assert lesson.mistakeIds == []
    assert milestone.mistakeIds == []


def test_generate_lesson_theory_response_mistake_review_mac_dinh_none():
    from app.schemas import GenerateLessonTheoryResponse

    res = GenerateLessonTheoryResponse(theoryMarkdown="# X")
    assert res.mistakeReview is None


# ══════════════════ (4) ĐI HẾT DÂY — endpoint HTTP → provider ══════════════════
# 🔎 Mẫu tests/test_roadmap_mode.py:222 (`_patch_roadmap`/`test_endpoint_roadmap_chuyen_tiep_mode_
# xuong_provider`): monkeypatch provider bằng fake CHỮ KÝ TƯỜNG MINH (không *a,**k) để phép mutation
# "quên forward mistakes ở main.py" thật sự bắt được — fake nhận đúng tên tham số `mistakes`.

def test_endpoint_generate_roadmap_chuyen_tiep_mistakes_xuong_provider(monkeypatch):
    received: dict = {}

    async def fake_generate_roadmap(job_category, level, weaknesses,
                                    focus=None, cv_analysis_summary=None,
                                    prior_roadmap_summary=None, grounding=None,
                                    criteria=None, scope=None, evidence=None, mode=None,
                                    current_level=None, mistakes=None):
        received["mistakes"] = mistakes
        return [{"title": "M1", "focusCriteria": [], "lessons": [{"title": "L1"}]}]

    monkeypatch.setattr(main_module.provider, "generate_roadmap", fake_generate_roadmap)

    res = client.post("/api/v1/generate-roadmap", headers=_HEADERS, json={
        "jobCategory": "BE", "level": "Junior",
        "mistakes": [_MISTAKE_JSON],
    })

    assert res.status_code == 200
    assert received["mistakes"] == [_MISTAKE_JSON]


def test_endpoint_generate_roadmap_khong_gui_mistakes_thi_provider_nhan_none(monkeypatch):
    received: dict = {}

    async def fake_generate_roadmap(job_category, level, weaknesses,
                                    focus=None, cv_analysis_summary=None,
                                    prior_roadmap_summary=None, grounding=None,
                                    criteria=None, scope=None, evidence=None, mode=None,
                                    current_level=None, mistakes=None):
        received["mistakes"] = mistakes
        return [{"title": "M1", "focusCriteria": [], "lessons": [{"title": "L1"}]}]

    monkeypatch.setattr(main_module.provider, "generate_roadmap", fake_generate_roadmap)

    res = client.post("/api/v1/generate-roadmap", headers=_HEADERS,
                      json={"jobCategory": "BE", "level": "Junior"})

    assert res.status_code == 200
    assert received["mistakes"] is None


def test_endpoint_generate_roadmap_response_giu_mistakeids_tu_provider(monkeypatch):
    """RoadmapMilestone dựng TƯỜNG MINH (item 4 của MIS1-B1) — kiểm chứ đừng sửa mù: `lessons`
    dựng bằng `**l` (splat) nên TỰ nhận `mistakeIds` nếu provider trả."""
    async def fake_generate_roadmap(job_category, level, weaknesses, **kwargs):
        return [{
            "title": "M1", "focusCriteria": [],
            "lessons": [{"title": "L1", "mistakeIds": ["m1", "m2"]}],
            "mistakeIds": ["m1", "m2"],
        }]

    monkeypatch.setattr(main_module.provider, "generate_roadmap", fake_generate_roadmap)

    res = client.post("/api/v1/generate-roadmap", headers=_HEADERS,
                      json={"jobCategory": "BE", "level": "Junior"})

    assert res.status_code == 200
    body = res.json()
    assert body["milestones"][0]["mistakeIds"] == ["m1", "m2"]
    assert body["milestones"][0]["lessons"][0]["mistakeIds"] == ["m1", "m2"]


def test_endpoint_generate_lesson_theory_chuyen_tiep_mistakes_xuong_provider(monkeypatch):
    received: dict = {}

    async def fake(job_category, level, lesson_title, focus_criteria, weaknesses,
                   grounding=None, evidence=None, mode=None, current_level=None,
                   mistakes=None):
        received["mistakes"] = mistakes
        return "# Chuẩn hoá DB\n\nND", [], None, None

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "BE", "level": "Junior", "lessonTitle": "Chuẩn hoá DB",
        "focusCriteria": ["Thiết kế CSDL"],
        "mistakes": [_MISTAKE_JSON],
    })

    assert res.status_code == 200
    assert received["mistakes"] == [_MISTAKE_JSON]


def test_endpoint_generate_lesson_theory_mistake_review_luon_an_o_buoc_nay(monkeypatch):
    """MIS1-B1: provider tạm trả `mistake_review=None` (chưa sinh, đó là B2/B3) ⇒
    `response_model_exclude_none` ẩn field, shape response KHÔNG đổi."""
    async def fake(job_category, level, lesson_title, focus_criteria, weaknesses,
                   grounding=None, **kwargs):
        return "# Bài\n\nND", [], None, None

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "BE", "level": "Junior", "lessonTitle": "Bài", "focusCriteria": []})

    assert res.status_code == 200
    assert "mistakeReview" not in res.json()


# ══════════════════ (5) PROVIDER → BUILDER — dây tới tận nơi ══════════════════
# generate_roadmap/generate_lesson_theory phải CHUYỀN mistakes xuống build_roadmap_prompt/
# build_lesson_theory_prompt — nội dung builder không đổi (mục 6), nhưng THAM SỐ phải tới nơi.

@pytest.mark.asyncio
async def test_provider_generate_roadmap_chuyen_mistakes_xuong_builder(monkeypatch):
    captured: dict = {}

    def fake_build(*args, **kwargs):
        captured.update(kwargs)
        return "PROMPT"

    async def fake_generate(self, operation, *, contents, config,
                            model=None, defer_report=False):
        # MIS1-B2 — milestone PHẢI mang `mistakeIds` khớp id đã cấp, nếu không
        # `filter_milestone_mistakes` sẽ lọc rỗng → kích hoạt retry/drop (hành vi ĐÚNG của B2,
        # nhưng không phải điều test này muốn đo — test này chỉ đo dây provider→builder).
        return _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1"}]}]})

    monkeypatch.setattr(gemini_module, "build_roadmap_prompt", fake_build)
    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider()

    given = [_MISTAKE_JSON]
    await provider.generate_roadmap("BE", "Junior", None, mistakes=given)

    assert captured.get("mistakes") == given


@pytest.mark.asyncio
async def test_provider_generate_lesson_theory_chuyen_mistakes_xuong_builder(monkeypatch):
    captured: dict = {}

    def fake_build(*args, **kwargs):
        captured.update(kwargs)
        return "PROMPT"

    async def fake_generate(self, operation, *, contents, config,
                            model=None, defer_report=False):
        return _fake_gemini_response({
            "sections": [{"criterion": "L", "heading": "L", "body": "Nội dung."}],
            "example": "Ví dụ.", "commonMistakes": "Lỗi hay gặp.", "resources": [],
        })

    monkeypatch.setattr(gemini_module, "build_lesson_theory_prompt", fake_build)
    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider()

    given = [_MISTAKE_JSON]
    result = await provider.generate_lesson_theory(
        "BE", "Junior", "L", ["L"], None, mistakes=given)

    assert captured.get("mistakes") == given
    # MIS1-B1 — provider tạm trả mistake_review=None (sinh nó là việc của B2/B3).
    assert result.mistake_review is None


# ══════════════════ (6) GOLDEN — builder CHƯA dùng mistakes trong prompt ══════════════════
# Mẫu tests/test_lesson_context_wire.py:62-68 (`test_prompt_khong_co_bai_hoc_thi_giu_nguyen_xi`):
# so NGUYÊN VĂN, không phải "không chứa chuỗi X" (quá yếu — vẫn đúng khi prompt mọc thêm chỗ khác).
#
# 🔴 MIS1-B2 — `test_golden_build_roadmap_prompt_co_mistakes_van_giu_nguyen_xi` (khẳng định
# `mistakes` CÓ giá trị KHÔNG đổi prompt) đã bị XOÁ khỏi đây: đó ĐÚNG LÀ bất biến của bước B1
# (nội dung prompt CHƯA đụng), và MIS1-B2 CỐ Ý đảo NGƯỢC nó — `mistakes` giờ là nguồn GOM CHỦ ĐỀ
# chính. Bất biến MỚI (mistakes CÓ giá trị ⇒ prompt ĐỔI) được khoá ở
# `tests/test_roadmap.py::test_roadmap_prompt_mistakes_thay_the_evidence_lam_nguon_gom_chu_de`.
# Vế "không truyền mistakes ⇒ giữ nguyên xi" NGAY DƯỚI ĐÂY vẫn đúng cho `build_roadmap_prompt`
# (không truyền và truyền `mistakes=None` phải ra cùng một prompt — không liên quan gì đến việc
# TRUYỀN GIÁ TRỊ).

def test_golden_build_roadmap_prompt_khong_truyen_mistakes_giu_nguyen_xi():
    base = build_roadmap_prompt("BE", "Junior", None)
    same = build_roadmap_prompt("BE", "Junior", None, mistakes=None)
    assert base == same


def test_golden_build_lesson_theory_prompt_khong_truyen_mistakes_giu_nguyen_xi():
    base = build_lesson_theory_prompt("BE", "Junior", "Bài", ["A"], None)
    same = build_lesson_theory_prompt("BE", "Junior", "Bài", ["A"], None, mistakes=None)
    assert base == same


def test_golden_build_lesson_theory_prompt_co_mistakes_van_giu_nguyen_xi():
    base = build_lesson_theory_prompt("BE", "Junior", "Bài", ["A"], None)
    with_mistakes = build_lesson_theory_prompt(
        "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE_JSON])
    assert base == with_mistakes
