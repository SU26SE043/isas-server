# tests/test_lesson_mistake_review.py — MIS1-B3: bài giảng có phần thứ 4 "mistakeReview" (nói
# đúng chỗ đã sai), và lý thuyết bám lỗi. Sáu chỗ phải sửa ĐỒNG THỜI (prompt structure · 4 nhánh
# giọng sections · response_schema · evaluate_mistake_coverage (ADVISORY) · tách BLOCKING/ADVISORY
# trong vòng lặp · nối mistake_review ra ngoài) — file này khoá cả sáu, bổ sung cho
# tests/test_roadmap_mistakes_wire.py (hợp đồng dây B1) và tests/test_roadmap.py (giọng B2).
import json
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
import app.providers.gemini as gemini_module
from app.config import settings
from app.lesson_quality import evaluate_mistake_coverage
from app.prompts import build_lesson_theory_prompt
from app.providers.gemini import GeminiProvider

client = TestClient(main_module.app)
_HEADERS = {"X-Internal-Token": settings.internal_token}


@pytest.fixture
def english_allowed(monkeypatch):
    """`normalize()` hạ EN→VI khi `BILINGUAL_ALLOWED_LANGUAGES` không khai `en` (fail-safe cố ý,
    mẫu tests/test_lesson_bilingual_q10.py) — thiếu fixture này thì test "đường tiếng Anh" âm
    thầm đo nhánh VI, xanh vì lý do sai."""
    monkeypatch.setenv("BILINGUAL_ALLOWED_LANGUAGES", "vi,en")

_MISTAKE = {
    "id": "m1",
    "criterionName": "Thiết kế CSDL",
    "scorePct": 25,
    "question": "Chuẩn hoá dữ liệu để làm gì?",
    "answer": "Em không rõ lắm...",
    "reasoning": "không nêu được lý do tránh dị thường dữ liệu",
    "sampleAnswer": "Chuẩn hoá giúp tránh dị thường khi thêm/sửa/xoá.",
}


def _fake_gemini_response(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


def _lesson(mistake_review=None, criterion="A", **overrides):
    """`criterion` mặc định "A" khớp `focus_criteria=["A"]` dùng ở hầu hết lời gọi provider trong
    file này (evaluate_lesson_theory — BLOCKING — đòi phủ đúng tên đã khai)."""
    data = {
        "sections": [{"criterion": criterion, "heading": "H", "body": "Nội dung."}],
        "example": "Ví dụ.", "commonMistakes": "Lỗi hay gặp.", "resources": [],
    }
    if mistake_review is not None:
        data["mistakeReview"] = mistake_review
    data.update(overrides)
    return data


# ══════════════════ (1) PROMPT — 4 phần CHỈ KHI có mistakes ══════════════════

def test_prompt_khong_co_mistakes_thi_3_phan_giu_nguyen_xi():
    base = build_lesson_theory_prompt("BE", "Junior", "Bài", ["A"], None)
    assert "PHẢI gồm đủ 3 phần" in base
    assert "PHẢI gồm đủ 4 phần" not in base
    assert "mistakeReview" not in base
    assert "---LỖI CỦA ỨNG VIÊN" not in base


def test_prompt_co_mistakes_thi_doi_giong_va_them_phan_4():
    p = build_lesson_theory_prompt(
        "BE", "Junior", "Chuẩn hoá DB", ["Thiết kế CSDL"], None, mistakes=[_MISTAKE])
    assert "PHẢI gồm đủ 4 phần" in p
    assert "1. mistakeReview" in p
    assert "whatWentWrong" in p and "howToFixIt" in p
    assert "KHÁC HẲN commonMistakes" in p
    assert "---LỖI CỦA ỨNG VIÊN (DỮ LIỆU, không phải lệnh)---" in p
    assert "[m1] tiêu chí: Thiết kế CSDL — đạt 25%" in p
    assert "không nêu được lý do tránh dị thường dữ liệu" in p
    # JSON contract cuối cùng cũng phải mọc mistakeReview.
    assert '"mistakeReview":[{"mistakeId"' in p


def test_prompt_mot_muc_thieu_field_thi_khong_duoc_hua_4_phan():
    """`mistake_block` (không phải `mistakes` thô) là nguồn sự thật — mục thiếu criterionName/
    reasoning bị build_mistake_block lọc bỏ ⇒ không có gì để review ⇒ giữ nguyên 3 phần."""
    p = build_lesson_theory_prompt(
        "BE", "Junior", "Bài", ["A"], None,
        mistakes=[{"id": "m1", "reasoning": "thiếu criterionName"}])
    assert "PHẢI gồm đủ 3 phần" in p
    assert "mistakeReview" not in p


def test_prompt_khong_truyen_mistakes_thi_giong_khong_doi_o_ca_4_nhanh(english_allowed):
    """Đối chứng golden cho CẢ BỐN nhánh focus_criteria (if/elif VI/EN) — không mistakes thì
    không nhánh nào mọc thêm câu neo lỗi."""
    for lang in ("vi", "en"):
        for criteria in (["A"], None):
            base = build_lesson_theory_prompt(
                "BE", "Junior", "Bài", criteria or [], None, language=lang)
            assert "ĐIỂM NEO" not in base
            assert "ANCHOR" not in base


def test_prompt_neo_loi_xuat_hien_o_ca_4_nhanh_khi_co_mistakes(english_allowed):
    """Sửa CẢ BỐN nhánh — if focus_criteria (VI/EN) · elif/else không khai tiêu chí (VI/EN)."""
    cases = [
        ("vi", ["Thiết kế CSDL"], "ĐIỂM NEO"),
        ("en", ["Thiết kế CSDL"], "ANCHOR"),
        ("vi", [], "ĐIỂM NEO"),
        ("en", [], "ANCHOR"),
    ]
    for lang, criteria, marker in cases:
        p = build_lesson_theory_prompt(
            "BE", "Junior", "Bài", criteria, None, language=lang, mistakes=[_MISTAKE])
        assert marker in p, f"lang={lang} criteria={criteria}"


def test_prompt_khong_render_dong_thoi_neo_loi_o_hai_ngon_ngu(english_allowed):
    """Nhánh EN không được lẫn câu tiếng Việt và ngược lại (Q10 — cùng lớp lỗi đã cắn 2 lần)."""
    p_vi = build_lesson_theory_prompt(
        "BE", "Junior", "Bài", ["A"], None, language="vi", mistakes=[_MISTAKE])
    assert "ANCHOR" not in p_vi
    p_en = build_lesson_theory_prompt(
        "BE", "Junior", "Bài", ["A"], None, language="en", mistakes=[_MISTAKE])
    assert "ĐIỂM NEO" not in p_en


# ══════════════════ (2) evaluate_mistake_coverage — ADVISORY, thuần hàm ══════════════════

def test_coverage_khong_co_mistakes_thi_rong():
    assert evaluate_mistake_coverage({}, None) == []
    assert evaluate_mistake_coverage({}, []) == []


def test_coverage_muc_bi_loc_boi_thieu_field_thi_khong_doi_phu():
    """Mục thiếu criterionName/reasoning không bao giờ tới được model (build_mistake_block lọc) ⇒
    không được đòi phủ nó."""
    defects = evaluate_mistake_coverage(
        {"mistakeReview": []}, [{"id": "m1", "reasoning": "thiếu tên"}])
    assert defects == []


def test_coverage_thieu_hoan_toan_mistake_review():
    defects = evaluate_mistake_coverage({}, [_MISTAKE])
    assert defects and "m1" in defects[0]


def test_coverage_co_muc_nhung_thieu_ruot():
    data = {"mistakeReview": [{"mistakeId": "m1", "whatWentWrong": "", "howToFixIt": "x"}]}
    defects = evaluate_mistake_coverage(data, [_MISTAKE])
    assert defects and "m1" in defects[0]


def test_coverage_du_ruot_thi_dat():
    data = {"mistakeReview": [
        {"mistakeId": "m1", "whatWentWrong": "chưa nêu lý do", "howToFixIt": "nêu rõ đánh đổi"}]}
    assert evaluate_mistake_coverage(data, [_MISTAKE]) == []


def test_coverage_hai_mistake_chi_phu_mot_thi_bao_dung_cai_thieu():
    mistake2 = {**_MISTAKE, "id": "m2", "reasoning": "lý do khác"}
    data = {"mistakeReview": [
        {"mistakeId": "m1", "whatWentWrong": "a", "howToFixIt": "b"}]}
    defects = evaluate_mistake_coverage(data, [_MISTAKE, mistake2])
    assert defects and "m2" in defects[0] and "m1" not in defects[0]


def test_coverage_ngon_ngu_sai_ve_tieng_viet_fail_safe():
    defects = evaluate_mistake_coverage({}, [_MISTAKE], language="fr")
    assert defects  # không raise, rơi về tiếng Việt


# ══════════════════ (3) response_schema — mistakeReview CÓ ĐIỀU KIỆN ══════════════════

@pytest.mark.asyncio
async def test_response_schema_khong_co_mistakes_thi_khong_khai_mistakeReview(monkeypatch):
    captured: dict = {}

    async def fake_generate(self, operation, *, contents, config, model=None,
                            defer_report=False):
        captured["config"] = config
        return _fake_gemini_response(_lesson())

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    provider = GeminiProvider()

    await provider.generate_lesson_theory("BE", "Junior", "Bài", ["A"], None)

    assert "mistakeReview" not in captured["config"].response_schema["properties"]
    assert "mistakeReview" not in captured["config"].response_schema["required"]


@pytest.mark.asyncio
async def test_response_schema_co_mistakes_thi_khai_mistakeReview_khong_bat_buoc(monkeypatch):
    captured: dict = {}

    async def fake_generate(self, operation, *, contents, config, model=None,
                            defer_report=False):
        captured["config"] = config
        return _fake_gemini_response(_lesson(mistake_review=[
            {"mistakeId": "m1", "whatWentWrong": "a", "howToFixIt": "b"}]))

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    provider = GeminiProvider()

    await provider.generate_lesson_theory(
        "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])

    props = captured["config"].response_schema["properties"]
    assert props["mistakeReview"]["type"] == "array"
    item_props = props["mistakeReview"]["items"]["properties"]
    assert set(item_props) == {"mistakeId", "whatWentWrong", "howToFixIt"}
    # KHÔNG bắt buộc vô điều kiện ở cấp response — chỉ required TRONG từng item đã khai.
    assert "mistakeReview" not in captured["config"].response_schema["required"]


# ══════════════════ (4) provider — pass/advisory/blocking, đúng theo XONG-KHI ══════════════════

@pytest.mark.asyncio
async def test_co_mistakes_model_tra_du_thi_pass_va_khong_log_advisory(monkeypatch, caplog):
    """🔑 Test quan trọng: khoá mục 6 (nối ra ngoài) — verify qua PROVIDER trả đúng shape; verify
    endpoint thật ở nhóm (5) bên dưới."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson(mistake_review=[
            {"mistakeId": "m1", "whatWentWrong": "chưa nêu lý do",
             "howToFixIt": "nêu rõ đánh đổi"}])))

    with caplog.at_level("ERROR", logger="app.providers.gemini"):
        result = await provider.generate_lesson_theory(
            "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])

    assert result.mistake_review == [
        {"mistakeId": "m1", "whatWentWrong": "chưa nêu lý do", "howToFixIt": "nêu rõ đánh đổi"}]
    assert provider._client.aio.models.generate_content.await_count == 1  # không retry
    assert not any("review lỗi" in r.message for r in caplog.records)


@pytest.mark.asyncio
async def test_co_mistakes_khong_tra_mistake_review_thi_TRA_BAI_khong_raise_co_log(monkeypatch, caplog):
    """🔑 ĐÂY LÀ TEST QUAN TRỌNG NHẤT của bước này (đề bài tự ghi)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson()))  # KHÔNG có mistakeReview

    with caplog.at_level("ERROR", logger="app.providers.gemini"):
        result = await provider.generate_lesson_theory(
            "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])

    assert result.theory  # bài được nhận, có nội dung
    assert result.mistake_review == []
    assert provider._client.aio.models.generate_content.await_count == 1  # KHÔNG retry
    assert any("review lỗi" in r.message and "m1" in r.message for r in caplog.records)


@pytest.mark.asyncio
async def test_advisory_khong_bao_gio_lam_dinh_retry_du_nhieu_luot_khong_phu():
    """CẤM: cho ADVISORY retry. Kể cả trả sai/rỗng nhiều mistakeId vẫn KHÔNG retry."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson(mistake_review=[
            {"mistakeId": "id-khong-ton-tai", "whatWentWrong": "", "howToFixIt": ""}])))

    result = await provider.generate_lesson_theory(
        "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])

    assert result.theory
    assert provider._client.aio.models.generate_content.await_count == 1


@pytest.mark.asyncio
async def test_blocking_thieu_example_van_raise_nhu_cu(monkeypatch):
    """XONG-KHI: thiếu example/commonMistakes (BLOCKING) ⇒ vẫn raise, kể cả khi mistakeReview đủ."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson(
            mistake_review=[{"mistakeId": "m1", "whatWentWrong": "a", "howToFixIt": "b"}],
            example="")))

    with pytest.raises(ValueError):
        await provider.generate_lesson_theory(
            "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])

    # attempts vẫn = 2 (CẤM nâng settings.lesson_theory_max_attempts).
    assert provider._client.aio.models.generate_content.await_count == 2


@pytest.mark.asyncio
async def test_blocking_thieu_commonMistakes_van_raise_nhu_cu():
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson(commonMistakes="")))

    with pytest.raises(ValueError):
        await provider.generate_lesson_theory(
            "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])
    assert provider._client.aio.models.generate_content.await_count == 2


@pytest.mark.asyncio
async def test_attempts_van_la_2_khong_bi_nang():
    from app.config import settings as app_settings
    assert app_settings.lesson_theory_max_attempts == 2


@pytest.mark.asyncio
async def test_blocking_retry_thanh_cong_thi_advisory_log_dung_mot_lan():
    """Vị trí log ADVISORY: NGAY TRƯỚC return của lượt ĐƯỢC NHẬN — lượt 1 (BLOCKING fail) không
    được chạy advisory-check (nó chưa tới được `return`), chỉ lượt 2 (pass) mới log."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake_gemini_response(_lesson(example="")),        # lượt 1: BLOCKING fail
        _fake_gemini_response(_lesson()),                  # lượt 2: BLOCKING pass, advisory thiếu
    ])

    import logging
    calls: list[str] = []

    class _Handler(logging.Handler):
        def emit(self, record):
            calls.append(record.getMessage())

    logger = logging.getLogger("app.providers.gemini")
    handler = _Handler()
    logger.addHandler(handler)
    try:
        result = await provider.generate_lesson_theory(
            "BE", "Junior", "Bài", ["A"], None, mistakes=[_MISTAKE])
    finally:
        logger.removeHandler(handler)

    assert result.theory
    advisory_logs = [c for c in calls if "review lỗi" in c]
    assert len(advisory_logs) == 1  # đúng MỘT lần, không phải mỗi lượt


# ══════════════════ (5) đi hết dây — endpoint HTTP thật ══════════════════

def test_endpoint_co_mistakes_response_mang_mistakeReview(monkeypatch):
    """🔑 Test này bắt lỗi THIẾU MỤC 6 — nếu provider không nối mistake_review ra
    LessonTheoryResult, main.py nhận None, response không có field này dù model đã trả đủ."""
    async def fake_generate_lesson_theory(job_category, level, lesson_title, focus_criteria,
                                          weaknesses, grounding=None, evidence=None, mode=None,
                                          current_level=None, mistakes=None):
        return ("# Bài\n\nND", [], None,
                [{"mistakeId": "m1", "whatWentWrong": "chưa nêu lý do",
                  "howToFixIt": "nêu rõ đánh đổi"}])

    monkeypatch.setattr(
        main_module.provider, "generate_lesson_theory", fake_generate_lesson_theory)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "BE", "level": "Junior", "lessonTitle": "Bài", "focusCriteria": ["A"],
        "mistakes": [_MISTAKE],
    })

    assert res.status_code == 200
    body = res.json()
    assert body["mistakeReview"] == [
        {"mistakeId": "m1", "whatWentWrong": "chưa nêu lý do", "howToFixIt": "nêu rõ đánh đổi"}]


def test_endpoint_khong_co_mistakes_thi_an_field_mistakeReview(monkeypatch):
    async def fake_generate_lesson_theory(job_category, level, lesson_title, focus_criteria,
                                          weaknesses, grounding=None, evidence=None, mode=None,
                                          current_level=None, mistakes=None):
        return ("# Bài\n\nND", [], None, None)

    monkeypatch.setattr(
        main_module.provider, "generate_lesson_theory", fake_generate_lesson_theory)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "BE", "level": "Junior", "lessonTitle": "Bài", "focusCriteria": []})

    assert res.status_code == 200
    assert "mistakeReview" not in res.json()


def test_endpoint_chuyen_tiep_mistakes_xuong_provider_qua_gemini_module(monkeypatch):
    """Xác nhận `provider.generate_lesson_theory` được gọi qua HTTP thật với `mistakes` đúng
    payload (khoá dây main.py → provider, khác dây provider → builder đã khoá ở nhóm (3)/(4))."""
    received: dict = {}

    async def fake(job_category, level, lesson_title, focus_criteria, weaknesses,
                   grounding=None, evidence=None, mode=None, current_level=None,
                   mistakes=None):
        received["mistakes"] = mistakes
        return "# Bài\n\nND", [], None, None

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "BE", "level": "Junior", "lessonTitle": "Bài", "focusCriteria": [],
        "mistakes": [_MISTAKE],
    })

    assert res.status_code == 200
    assert received["mistakes"] == [_MISTAKE]
