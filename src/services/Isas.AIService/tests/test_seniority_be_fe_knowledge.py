# tests/test_seniority_be_fe_knowledge.py — BK36: seed `_KNOWLEDGE_DEFAULTS` cho BE và FE.
#
# `test_seniority_be3_knowledge_ba.py` khoá phần seed BA (BE-3.2) và dùng "BE" làm ca "nghề chưa
# seed" để chứng minh không rò chéo — cách đó đúng ở thời điểm chỉ BA có seed. Nay BE/FE cũng có
# seed nên cần bộ khoá RIÊNG, mẫu y hệt file BA:
#   (1) nội dung BE/FE thật sự chảy vào CẢ HAI prompt (roadmap + bài giảng);
#   (2) không rò chéo NGHỀ (BE không lộ nội dung FE và ngược lại, cả hai không lộ nội dung BA);
#   (3) không rò chéo MỨC (Senior không lộ ra ở Fresher, dù cùng nghề);
#   (4) registry vẫn ghi đè được default trong source.
#
# Không gọi Gemini thật.
import pytest

from app import prompt_registry
from app import seniority as seniority_module
from app.prompts import build_lesson_theory_prompt, build_roadmap_prompt


@pytest.fixture(autouse=True)
def _clean_registry():
    """Registry là cache module-level — không dọn thì test này rò sang test kia."""
    prompt_registry.reset_cache()
    yield
    prompt_registry.reset_cache()


# ══════════════════════════════════════════════════════════════════════════════
# (1) Nội dung BE/FE thật sự chảy vào roadmap + bài giảng
# ══════════════════════════════════════════════════════════════════════════════

def test_roadmap_prompt_be_co_kien_thuc_chuyen_sau_mac_dinh():
    prompt = build_roadmap_prompt(
        job_category="BE", level="Senior", weaknesses=None, cv_text=None)
    assert "đồng bộ dữ liệu" in prompt


def test_lesson_theory_prompt_be_co_kien_thuc_chuyen_sau_mac_dinh():
    prompt = build_lesson_theory_prompt(
        job_category="BE", level="Junior", lesson_title="Bài", focus_criteria=[],
        weaknesses=None)
    assert "GROUP BY" in prompt


def test_roadmap_prompt_fe_co_kien_thuc_chuyen_sau_mac_dinh():
    prompt = build_roadmap_prompt(
        job_category="FE", level="Senior", weaknesses=None, cv_text=None)
    assert "module federation" in prompt


def test_lesson_theory_prompt_fe_co_kien_thuc_chuyen_sau_mac_dinh():
    prompt = build_lesson_theory_prompt(
        job_category="FE", level="Junior", lesson_title="Bài", focus_criteria=[],
        weaknesses=None)
    assert "re-render không cần thiết" in prompt


# ══════════════════════════════════════════════════════════════════════════════
# (2) Không rò chéo NGHỀ
# ══════════════════════════════════════════════════════════════════════════════

def test_calibration_block_be_khong_ro_ri_kien_thuc_fe_va_ba():
    block = seniority_module.calibration_block("Senior", "BE")
    assert "module federation" not in block          # của FE
    assert "dẫn dắt BA/PO junior" not in block        # của BA


def test_calibration_block_fe_khong_ro_ri_kien_thuc_be_va_ba():
    block = seniority_module.calibration_block("Senior", "FE")
    assert "đồng bộ dữ liệu" not in block             # của BE
    assert "dẫn dắt BA/PO junior" not in block        # của BA


# ══════════════════════════════════════════════════════════════════════════════
# (3) Không rò chéo MỨC
# ══════════════════════════════════════════════════════════════════════════════

def test_calibration_block_be_khong_ro_ri_sang_muc_khac():
    """Kiến thức Senior không lộ ra khi hiệu chỉnh cho Fresher, dù cùng nghề BE."""
    fresher_block = seniority_module.calibration_block("Fresher", "BE")
    assert "đồng bộ dữ liệu" not in fresher_block
    assert "cấu trúc dữ liệu cơ bản" in fresher_block


def test_calibration_block_fe_khong_ro_ri_sang_muc_khac():
    fresher_block = seniority_module.calibration_block("Fresher", "FE")
    assert "module federation" not in fresher_block
    assert "HTML semantic" in fresher_block


# ══════════════════════════════════════════════════════════════════════════════
# (4) Registry vẫn ghi đè được default trong source
# ══════════════════════════════════════════════════════════════════════════════

def test_calibration_block_be_registry_ghi_de_duoc_default():
    prompt_registry._cache = {
        "category.BE.seniority.Senior.knowledge": "BE_SENIOR_MARKER_TUY_BIEN",
    }
    block = seniority_module.calibration_block("Senior", "BE")
    assert "BE_SENIOR_MARKER_TUY_BIEN" in block
    assert "đồng bộ dữ liệu" not in block
