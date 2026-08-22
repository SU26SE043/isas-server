# tests/test_seniority_be3_knowledge_ba.py — BE-3 commit 2: seed `_KNOWLEDGE_DEFAULTS` cho BA.
#
# Thang mặc định của `calibration_block` (`_PROFILE_DEFAULTS`) nghiêng hẳn về kỹ thuật ("đánh đổi
# kiến trúc", "vận hành quy mô lớn") — hợp với BE/FE nhưng sai bản chất với BA, và BA chiếm
# 14/29 roadmap đo được (2026-08-21). Khe `category.{JOB}.seniority.{level}.knowledge` đã tồn tại
# từ J4 nhưng registry luôn rỗng vì chưa ai seed — file này khoá phần seed BA, mẫu
# `test_seniority_registry_j4.py`:
#   (1) nội dung BA thật sự chảy vào CẢ HAI prompt (roadmap + bài giảng);
#   (2) không rò sang nghề chưa được seed (BE/FE);
#   (3) không rò chéo MỨC (Senior không lộ ra ở Fresher, dù cùng nghề);
#   (4) registry vẫn ghi đè được default trong source — seed không phải hard-code bất biến.
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


def test_roadmap_prompt_ba_co_kien_thuc_chuyen_sau_mac_dinh():
    prompt = build_roadmap_prompt(
        job_category="BA", level="Senior", weaknesses=None)
    assert "dẫn dắt BA/PO junior" in prompt


def test_lesson_theory_prompt_ba_co_kien_thuc_chuyen_sau_mac_dinh():
    prompt = build_lesson_theory_prompt(
        job_category="BA", level="Junior", lesson_title="Bài", focus_criteria=[],
        weaknesses=None)
    assert "chạy workshop thu thập yêu cầu" in prompt


def test_roadmap_prompt_be_khong_ro_ri_kien_thuc_ba():
    """Nghề KHÁC (chưa được seed) không được vô tình nhận nội dung của BA."""
    prompt = build_roadmap_prompt(
        job_category="BE", level="Senior", weaknesses=None)
    assert "dẫn dắt BA/PO junior" not in prompt


def test_calibration_block_ba_registry_ghi_de_duoc_default():
    """Mẫu `test_seniority_registry_j4.py` — default trong source KHÔNG bịt registry."""
    prompt_registry._cache = {
        "category.BA.seniority.Senior.knowledge": "BA_SENIOR_MARKER_TUY_BIEN",
    }
    block = seniority_module.calibration_block("Senior", "BA")
    assert "BA_SENIOR_MARKER_TUY_BIEN" in block
    assert "dẫn dắt BA/PO junior" not in block


def test_calibration_block_ba_khong_ro_ri_sang_muc_khac():
    """Kiến thức Senior không lộ ra khi hiệu chỉnh cho Fresher, dù cùng nghề BA."""
    fresher_block = seniority_module.calibration_block("Fresher", "BA")
    assert "dẫn dắt BA/PO junior" not in fresher_block
    assert "user story" in fresher_block
