# tests/test_seniority_be3_wiring.py — BE-3 commit 1: cấp độ ứng viên phải tới được ROADMAP + BÀI
# GIẢNG, không chỉ tới bộ câu hỏi (SEN1).
#
# VẤN ĐỀ ĐANG SỐNG trước BE-3: `seniority`/`level` đã đi vào `build_prompt` (SEN1) và
# `build_criterion_levels_prompt`/`build_criteria_prompt` từ trước, nhưng `build_roadmap_prompt`
# và `build_lesson_theory_prompt` chỉ IN TÊN cấp độ ("trình độ Senior") mà không nói cấp độ đó
# NGHĨA LÀ GÌ — lý do một roadmap Senior đọc như mục lục sách giáo khoa (đo 2026-08-21).
#
# 🔴 REC1-B3 (2026, sau bản BE-3 gốc) TÁCH LÀM ĐÔI phần "bài giảng" của file này: khối
# `calibration_block` mà roadmap dùng mở đầu bằng chỉ thị TRẦN/SÀN cho prompt SINH CÂU HỎI
# ("Toàn bộ CÂU HỎI phải được hiệu chỉnh…") và kết bằng "KHÔNG ĐƯỢC VƯỢT LÊN CẤP CAO HƠN" — một
# bài giảng không sinh câu hỏi nào bị bó sai chỗ. `build_lesson_theory_prompt` nay dùng
# `knowledge_block` (chỉ phần nội dung THẬT, không có câu trần/sàn) — xem
# `test_seniority_calibration_knowledge_split_rec1_b3.py` cho bằng chứng byte-identical của
# `calibration_block` (vẫn dùng cho roadmap, KHÔNG đổi). Các test build_lesson_theory_prompt dưới
# đây đã cập nhật theo bất biến MỚI; build_roadmap_prompt GIỮ NGUYÊN.
#
# Hai thứ được khoá ở đây, mẫu `test_seniority_wire_sen1.py`:
#   (1) PROMPT THẬT SỰ CÓ nội dung hiệu chỉnh theo cấp độ — không chỉ nhận `level` mà bỏ xó.
#   (2) THỨ TỰ ĐÚNG — chỉ thị hệ thống PHẢI đứng trước mọi khối DỮ LIỆU do ứng viên/HR kiểm soát
#       (AI-4, chống prompt-injection). Đây là mutation dễ lọt nhất: đặt SAU khối dữ liệu vẫn cho
#       ra đúng những substring mà một test hời hợt sẽ tìm — phải khoá bằng SO INDEX, không phải
#       "in".
#
# Không gọi Gemini thật. Nội dung kiến thức chuyên sâu theo nghề (`_KNOWLEDGE_DEFAULTS`) được
# khoá riêng ở `test_seniority_be3_knowledge_ba.py`.
import pytest

from app import seniority as seniority_module
from app.prompts import build_lesson_theory_prompt, build_roadmap_prompt


# ══════════════════════════════════════════════════════════════════════════════
# 1. Khối hiệu chỉnh THẬT SỰ có mặt
# ══════════════════════════════════════════════════════════════════════════════

def test_roadmap_prompt_co_khoi_hieu_chinh_cap_do():
    prompt = build_roadmap_prompt(
        job_category="BE", level="Senior", weaknesses=None)
    assert "CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: Senior" in prompt
    assert "hiệu chỉnh ĐÚNG TẦM" in prompt


def test_lesson_theory_prompt_khong_con_khoi_hieu_chinh_nhung_van_co_kien_thuc():
    """REC1-B3 — bài giảng KHÔNG còn khối `calibration_block` (viết cho prompt sinh câu hỏi),
    nhưng nội dung kiến thức thật (`knowledge_block`) vẫn phải còn."""
    prompt = build_lesson_theory_prompt(
        job_category="BE", level="Senior", lesson_title="Bài", focus_criteria=[],
        weaknesses=None)
    assert "CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: Senior" not in prompt
    assert "hiệu chỉnh ĐÚNG TẦM" not in prompt
    assert "thiết kế kiến trúc hệ thống nhiều service" in prompt   # _KNOWLEDGE_DEFAULTS["BE"]["Senior"]


@pytest.mark.parametrize("level", seniority_module.LEVELS)
def test_roadmap_prompt_moi_muc_deu_co_khoi(level):
    """Nhận `level` mà không dùng = hỏng y hệt như không nhận — khoá cho cả 4 mức."""
    prompt = build_roadmap_prompt(
        job_category="FE", level=level, weaknesses=None)
    assert f"CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: {level}" in prompt


@pytest.mark.parametrize("level", seniority_module.LEVELS)
def test_lesson_theory_prompt_moi_muc_deu_co_kien_thuc(level):
    """REC1-B3 — khoá cho cả 4 mức: `level` phải thật sự chọn ĐÚNG dòng kiến thức của MỨC ĐÓ
    (không phải khối calibration_block đã bị gỡ khỏi bài giảng)."""
    prompt = build_lesson_theory_prompt(
        job_category="FE", level=level, lesson_title="Bài", focus_criteria=[], weaknesses=None)
    assert f"Kiến thức FE mức {level}" in prompt
    assert f"CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: {level}" not in prompt


# ══════════════════════════════════════════════════════════════════════════════
# 2. THỨ TỰ — chỉ thị hệ thống đứng TRƯỚC mọi khối dữ liệu (AI-4)
# ══════════════════════════════════════════════════════════════════════════════

def test_roadmap_prompt_hieu_chinh_dung_truoc_chong_injection_va_du_lieu():
    prompt = build_roadmap_prompt(
        job_category="BE", level="Senior",
        weaknesses=[{"criterionName": "SQL", "percentage": 40}],
        cv_analysis_summary="Tóm tắt CV: 3 năm backend.",
    )
    idx_calib = prompt.index("CẤP ĐỘ ỨNG VIÊN")
    assert idx_calib < prompt.index("CHỐNG PROMPT INJECTION")
    assert idx_calib < prompt.index("---ĐIỂM YẾU (DỮ LIỆU")
    # CV THÔ đã bị gỡ khỏi luồng roadmap; mốc dữ liệu-ứng-viên nay là bản phân tích CV.
    assert idx_calib < prompt.index("---PHÂN TÍCH CV (DỮ LIỆU")


def test_lesson_theory_prompt_kien_thuc_dung_truoc_chong_injection_va_du_lieu():
    """REC1-B3 — mốc thứ tự đổi từ khối `calibration_block` (đã gỡ khỏi bài giảng) sang chính nội
    dung `knowledge_block` — vẫn phải là chỉ thị hệ thống, đứng TRƯỚC mọi khối DỮ LIỆU (AI-4)."""
    prompt = build_lesson_theory_prompt(
        job_category="BE", level="Senior", lesson_title="Chuẩn hoá DB",
        focus_criteria=["Thiết kế CSDL"],
        weaknesses=["Không nắm rõ 3NF."],
    )
    idx_knowledge = prompt.index("thiết kế kiến trúc hệ thống nhiều service")
    assert idx_knowledge < prompt.index("CHỐNG PROMPT INJECTION")
    assert idx_knowledge < prompt.index("---ĐIỂM YẾU (DỮ LIỆU")
