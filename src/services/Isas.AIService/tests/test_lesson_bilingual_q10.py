# tests/test_lesson_bilingual_q10.py — Q10: bài giảng roadmap tiếng Anh KHÔNG được mang tiếng Việt.
#
# Bằng chứng sinh ra bộ test này (deploy 2026-08-07): một roadmap `language=en` có thân bài tiếng
# Anh 5.162 ký tự nhưng hai mục cuối vẫn là "## Ví dụ minh hoạ" / "## Lỗi thường gặp khi trả lời
# phỏng vấn" — verify trong DB, không phải lỗi hiển thị.
#
# Ba đường rò, mỗi đường một nhóm test dưới đây:
#   1. Nhãn mục do SERVER ghép (`render_lesson_markdown`) ghi cứng tiếng Việt.
#   2. Câu chữ khiếm khuyết của rubric đi thẳng vào `retry_feedback` ⇒ đề bài lượt 2 song ngữ.
#   3. Chỉ dẫn "ghi ĐÚNG NGUYÊN VĂN tên tiêu chí, không dịch lại" viết bằng tiếng Việt nằm trong
#      một đề bài yêu cầu viết tiếng Anh — mô hình dịch tên tiêu chí là trượt rubric → 502.
#
# ⚠ `normalize()` hạ EN→VI khi `BILINGUAL_ALLOWED_LANGUAGES` không khai `en` (fail-safe cố ý), nên
# MỌI test đường tiếng Anh phải bật cờ đó; thiếu là test xanh vì lý do sai (nó đo nhánh VI).
import json
from unittest.mock import AsyncMock

import pytest

from app.lesson_quality import (
    EXAMPLE_HEADING, MISTAKES_HEADING, evaluate_lesson_theory, message, render_lesson_markdown,
)
from app.language import lesson_example_heading, lesson_mistakes_heading
from app.prompts import build_lesson_theory_prompt
from app.providers.gemini import GeminiProvider


@pytest.fixture
def english_allowed(monkeypatch):
    monkeypatch.setenv("BILINGUAL_ALLOWED_LANGUAGES", "vi,en")


def _fake_gemini_response(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


def _lesson_en(criteria=("Database design",), *, example="Example: split orders from customers.",
               mistakes="Confusing 2NF with 3NF when asked."):
    return {
        "sections": [
            {"criterion": c, "heading": f"About {c}", "body": f"How to explain {c} in an interview."}
            for c in criteria
        ],
        "example": example,
        "commonMistakes": mistakes,
    }


# ── Rò 1: nhãn mục do server ghép ───────────────────────────────────────────
def test_render_dung_nhan_tieng_anh_cho_bai_tieng_anh(english_allowed):
    md = render_lesson_markdown("Normalisation", _lesson_en(), language="en")

    assert f"## {lesson_example_heading('en')}" in md
    assert f"## {lesson_mistakes_heading('en')}" in md
    # Chính hai chuỗi đã lọt ra deploy — khoá bằng hằng chứ không gõ lại, để đổi câu chữ VI
    # không làm test này xanh giả.
    assert EXAMPLE_HEADING not in md
    assert MISTAKES_HEADING not in md


def test_render_giu_nguyen_nhan_tieng_viet_khi_khong_khai_ngon_ngu():
    """Đường VI là mặc định fail-safe: caller cũ không gửi `language` phải nhận y hệt bản cũ."""
    md = render_lesson_markdown("Chuẩn hoá DB", _lesson_en(["Thiết kế CSDL"]))

    assert f"## {EXAMPLE_HEADING}" in md
    assert f"## {MISTAKES_HEADING}" in md


@pytest.mark.asyncio
async def test_provider_ghep_bai_tieng_anh_bang_nhan_tieng_anh(english_allowed):
    """Đường ĐẦY ĐỦ như prod: provider nhận `language` → phải LUỒN xuống bộ ghép Markdown.

    Test hàm `render_lesson_markdown` một mình KHÔNG phủ được chỗ này: bug trên deploy nằm ở
    khúc ĐẤU DÂY (provider quên truyền), không nằm trong bộ ghép. Mutation Q10-M2 (gỡ
    `language=language` ở call site) XANH cho tới khi có test này — đúng dạng "bộ test hẹp hơn ta
    tưởng" mà quy ước mutation-check sinh ra để bắt.
    """
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson_en(["Database design"])))

    theory, _, _ = await provider.generate_lesson_theory(
        "BE", "Junior", "Normalisation", ["Database design"], None, language="en")

    assert f"## {lesson_example_heading('en')}" in theory
    assert f"## {lesson_mistakes_heading('en')}" in theory
    assert EXAMPLE_HEADING not in theory
    assert MISTAKES_HEADING not in theory


@pytest.mark.asyncio
async def test_provider_khong_khai_ngon_ngu_van_ghep_nhan_tieng_viet():
    """Đối xứng với test trên: đường B2C hiện tại (không gửi `language`) không được đổi."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(_lesson_en(["Thiết kế CSDL"])))

    theory, _, _ = await provider.generate_lesson_theory(
        "BE", "Junior", "Chuẩn hoá DB", ["Thiết kế CSDL"], None)

    assert f"## {EXAMPLE_HEADING}" in theory
    assert f"## {MISTAKES_HEADING}" in theory


def test_en_bi_ha_cap_thi_nhan_cung_ha_cap_theo(monkeypatch):
    """`BILINGUAL_ALLOWED_LANGUAGES` chưa mở `en` ⇒ toàn hệ chạy tiếng Việt. Nhãn phải đi theo
    quyết định đó, nếu không bài tiếng Việt lại mọc hai tiêu đề tiếng Anh."""
    monkeypatch.setenv("BILINGUAL_ALLOWED_LANGUAGES", "vi")

    md = render_lesson_markdown("Bài", _lesson_en(), language="en")

    assert f"## {EXAMPLE_HEADING}" in md
    assert lesson_example_heading("vi") == EXAMPLE_HEADING


# ── Rò 2: câu chữ khiếm khuyết (đi vào retry_feedback, không chỉ để đọc) ────
def test_khiem_khuyet_bai_tieng_anh_viet_bang_tieng_anh(english_allowed):
    data = _lesson_en(["Database design"], example="", mistakes="")
    defects = evaluate_lesson_theory(data, ["Database design", "Indexing"], "Lesson",
                                     language="en")

    assert defects, "bài thiếu tiêu chí + thiếu ví dụ + thiếu lỗi thường gặp phải bị bắt"
    joined = " ".join(defects)
    # Không được lẫn bản tiếng Việt của ĐÚNG những câu này.
    for vietnamese in ("Chưa có mục nào giải thích", "Thiếu ví dụ minh hoạ",
                       "Thiếu phần lỗi/hiểu lầm"):
        assert vietnamese not in joined
    assert "Indexing" in joined            # vẫn phải nêu ĐÚNG tiêu chí còn thiếu (dùng cho lượt 2)
    assert "focus criteria" in joined
    assert "worked example" in joined
    assert "commonMistakes" in joined


def test_khiem_khuyet_mac_dinh_van_la_tieng_viet():
    """Regression: mọi caller cũ (và toàn bộ đường B2C hiện tại) không gửi `language`."""
    defects = evaluate_lesson_theory(_lesson_en(["A"], example=""), ["A"], "Bài")
    assert any("Thiếu ví dụ minh hoạ" in d for d in defects)


def test_cach_cham_khong_doi_theo_ngon_ngu(english_allowed):
    """Rubric chỉ kiểm mục-có-ruột + `criterion` thuộc tập CALLER truyền vào + example/mistakes —
    không chỗ nào khớp theo tiêu đề tiếng Việt. Bài tiếng Anh ĐẠT không được vì đổi ngôn ngữ mà
    thành trượt (nếu không, bản vá hiển thị lại đẻ ra một đường 502 mới)."""
    assert evaluate_lesson_theory(_lesson_en(["Database design"]), ["Database design"],
                                  "Lesson", language="en") == []


@pytest.mark.asyncio
async def test_nhan_xet_lua_hai_cua_bai_tieng_anh_khong_lan_tieng_viet(english_allowed):
    """Lượt 1 thiếu tiêu chí → trả lại; đề lượt 2 phải nêu phần thiếu BẰNG TIẾNG ANH.

    Đây mới là lý do rò 2 quan trọng: nhận xét không chỉ để người đọc, nó là ĐỀ BÀI của lượt sau.
    """
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake_gemini_response(_lesson_en(["Database design"])),                 # thiếu Indexing
        _fake_gemini_response(_lesson_en(["Database design", "Indexing"])),     # đủ
    ])

    theory, _, _ = await provider.generate_lesson_theory(
        "BE", "Junior", "Normalisation", ["Database design", "Indexing"], None,
        language="en")

    assert "About Indexing" in theory
    prompt_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    assert "YOUR PREVIOUS ANSWER WAS REJECTED" in prompt_lan_2
    assert "BỊ TRẢ LẠI" not in prompt_lan_2
    assert "Chưa có mục nào giải thích" not in prompt_lan_2
    assert "Indexing" in prompt_lan_2


@pytest.mark.asyncio
async def test_bai_json_hong_cung_bao_loi_bang_tieng_anh(english_allowed):
    """Nhánh 'lượt trước không parse được' cũng đi vào retry_feedback ⇒ cũng phải song ngữ."""
    broken = AsyncMock()
    broken.text = "không phải json"

    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        broken,
        _fake_gemini_response(_lesson_en(["Database design"])),
    ])

    await provider.generate_lesson_theory(
        "BE", "Junior", "Normalisation", ["Database design"], None, language="en")

    prompt_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    assert message("not_json", "en", raw="không phải json") in prompt_lan_2
    assert "Bản trước không phải JSON hợp lệ" not in prompt_lan_2


# ── Rò 3: chỉ dẫn "đừng dịch tên tiêu chí" (nguy cơ 502, không phải thẩm mỹ) ─
def test_de_bai_tieng_anh_cam_dich_ten_tieu_chi_bang_tieng_anh(english_allowed):
    prompt = build_lesson_theory_prompt("BE", "Junior", "Normalisation",
                                        ["Thiết kế CSDL"], None, language="en")

    assert "DO NOT TRANSLATE IT" in prompt
    assert "VERBATIM" in prompt
    assert "không dịch lại" not in prompt
    assert "Thiết kế CSDL" in prompt       # tên tiêu chí vẫn nguyên văn trong đề


def test_de_bai_tieng_viet_khong_doi_mot_chu(english_allowed):
    """Nhánh VI phải byte-identical — đây là đường ĐANG CHẠY của toàn bộ B2C."""
    prompt = build_lesson_theory_prompt("BE", "Junior", "Bài", ["Thiết kế CSDL"], None)

    assert "không tự đặt tên khác, không viết tắt, không dịch lại." in prompt
    assert "DO NOT TRANSLATE" not in prompt


def test_milestone_khong_khai_tieu_chi_cung_song_ngu(english_allowed):
    en = build_lesson_theory_prompt("BE", "Junior", "Normalisation", [], None, language="en")
    vi = build_lesson_theory_prompt("BE", "Junior", "Chuẩn hoá", [], None)

    assert "verbatim" in en and "Milestone không khai tiêu chí" not in en
    assert "Milestone không khai tiêu chí" in vi
