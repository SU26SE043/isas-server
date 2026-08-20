# tests/test_seniority_cases_shape.py — J7 bộ ca thử: chỉ kiểm CẤU TRÚC, KHÔNG gọi Gemini.
#
# Production có 0 buổi Middle và 2 buổi Senior trên 222 buổi — không có cách nào nghiệm thu hai
# cấp cao bằng dữ liệu thật. `tests/fixtures/seniority_cases.json` là bộ ca thay thế (1 nghề BE/vi
# × 4 cấp × 3 mức chất lượng câu trả lời), để người có quyền server dùng làm đầu vào cho
# `scripts/benchmark-scoring.py` khi nghiệm thu J5/J7. Việc của test này CHỈ là xác nhận bộ ca
# ĐÚNG HÌNH DẠNG — không đánh giá AI có chấm đúng hay không (đó là việc ngoài phạm vi, cần Gemini
# key + người có quyền server, xem ràng buộc "KHÔNG có quyền truy cập server" của J1-J8).
import json
import pathlib

import pytest

from app.prompts import build_scoring_prompt
from app.seniority import LEVELS

_FIXTURE_PATH = pathlib.Path(__file__).parent / "fixtures" / "seniority_cases.json"


@pytest.fixture(scope="module")
def fixture_data() -> dict:
    return json.loads(_FIXTURE_PATH.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def cases(fixture_data) -> list[dict]:
    return fixture_data["cases"]


def test_fixture_file_ton_tai_va_la_json_hop_le(fixture_data):
    assert "cases" in fixture_data
    assert isinstance(fixture_data["cases"], list)


def test_du_12_ca(cases):
    assert len(cases) == 12


def test_du_4_cap_do_x_3_muc_chat_luong(cases):
    """4 cấp × 3 mức = 12 tổ hợp, mỗi tổ hợp xuất hiện ĐÚNG 1 lần — không thiếu, không trùng."""
    combos = {(c["seniority"], c["quality"]) for c in cases}
    assert len(combos) == 12

    for level in LEVELS:
        by_level = [c for c in cases if c["seniority"] == level]
        assert len(by_level) == 3, f"cấp {level} phải có đúng 3 ca (yếu/khá/giỏi), có {len(by_level)}"
        qualities = {c["quality"] for c in by_level}
        assert qualities == {"weak", "decent", "strong"}, f"cấp {level} thiếu mức chất lượng: {qualities}"


def test_moi_ca_co_du_truong_bat_buoc(cases):
    required = {"case_id", "seniority", "quality", "job_category", "language",
                "question", "transcript", "criteria", "expected_band"}
    for c in cases:
        missing = required - c.keys()
        assert not missing, f"ca '{c.get('case_id')}' thiếu trường: {missing}"


def test_moi_ca_seniority_thuoc_tap_hop_le(cases):
    for c in cases:
        assert c["seniority"] in LEVELS, f"ca '{c['case_id']}' có seniority lạ: {c['seniority']}"


def test_case_id_khong_trung_nhau(cases):
    ids = [c["case_id"] for c in cases]
    assert len(ids) == len(set(ids)), "case_id trùng nhau — không phân biệt được ca nào là ca nào"


# ══════════════════════════════════════════════════════════════════════════════
# criteria đúng shape mà provider.score() nhận
# ══════════════════════════════════════════════════════════════════════════════

def test_moi_ca_co_it_nhat_1_tieu_chi(cases):
    for c in cases:
        assert isinstance(c["criteria"], list) and len(c["criteria"]) >= 1, \
            f"ca '{c['case_id']}' phải có ít nhất 1 tiêu chí"


def test_moi_tieu_chi_dung_shape_production(cases):
    """Đúng shape mà `provider.score()` đọc: criterionId/name/description/maxScore/weight/levels."""
    for c in cases:
        for crit in c["criteria"]:
            for key in ("criterionId", "name", "maxScore", "levels"):
                assert key in crit, f"ca '{c['case_id']}' tiêu chí thiếu khoá '{key}'"
            assert isinstance(crit["maxScore"], int) and crit["maxScore"] > 0
            assert isinstance(crit["levels"], list) and len(crit["levels"]) >= 2, \
                f"ca '{c['case_id']}' tiêu chí '{crit.get('name')}' phải có ≥2 mức"
            scores_in_levels = set()
            for lv in crit["levels"]:
                assert "score" in lv and "descriptor" in lv
                assert 0 <= lv["score"] <= crit["maxScore"]
                assert isinstance(lv["descriptor"], str) and lv["descriptor"].strip()
                scores_in_levels.add(lv["score"])
            # Mốc 0 và mốc maxScore phải có mặt (CriterionLevelRules — mốc thiếu 0 làm bài
            # trống snap về mốc cao hơn, ứng viên không nói gì vẫn có điểm).
            assert 0 in scores_in_levels, f"ca '{c['case_id']}' tiêu chí thiếu mốc 0"
            assert crit["maxScore"] in scores_in_levels, \
                f"ca '{c['case_id']}' tiêu chí thiếu mốc {crit['maxScore']} (maxScore)"


# ══════════════════════════════════════════════════════════════════════════════
# expected_band hợp lệ — [min, max], min ≤ max, cả hai trong [0, maxScore chung của ca]
# ══════════════════════════════════════════════════════════════════════════════

def test_expected_band_la_khoang_hop_le(cases):
    for c in cases:
        band = c["expected_band"]
        assert isinstance(band, list) and len(band) == 2, \
            f"ca '{c['case_id']}' expected_band phải là [min, max], có: {band}"
        lo, hi = band
        assert lo <= hi, f"ca '{c['case_id']}' expected_band lộn ngược: {band}"
        max_scores = {crit["maxScore"] for crit in c["criteria"]}
        assert all(0 <= lo and hi <= m for m in max_scores), \
            f"ca '{c['case_id']}' expected_band {band} vượt thang điểm tiêu chí {max_scores}"


def test_expected_band_khong_phai_mot_con_so_duy_nhat_cho_moi_muc_chat_luong():
    """Kiểm tra tinh thần của fixture: 'yếu' phải thấp hơn 'giỏi' — nếu không thì bộ ca không đo
    được gì (mọi mức chất lượng đều kỳ vọng cùng một khoảng điểm)."""
    data = json.loads(_FIXTURE_PATH.read_text(encoding="utf-8"))
    for level in LEVELS:
        by_quality = {c["quality"]: tuple(c["expected_band"])
                      for c in data["cases"] if c["seniority"] == level}
        assert by_quality["weak"][1] < by_quality["strong"][0], (
            f"cấp {level}: cận trên của 'weak' {by_quality['weak']} phải THẤP HƠN cận dưới của "
            f"'strong' {by_quality['strong']}, nếu không hai mức chất lượng không phân biệt được")


# ══════════════════════════════════════════════════════════════════════════════
# Mọi ca dựng được prompt CHẤM không lỗi — KHÔNG gọi Gemini (build_scoring_prompt thuần string)
# ══════════════════════════════════════════════════════════════════════════════

def test_moi_ca_dung_duoc_prompt_cham_khong_loi(cases):
    """Không assert tên cấp độ xuất hiện trong PROMPT CHẤM — khác `build_prompt` (SINH), khe
    `seniority.{level}.scoring_focus` (J5) mặc định RỖNG nên registry sạch (đúng môi trường test
    này) không để lại dấu vết văn bản nào; đó CHÍNH LÀ bất biến J5 cần giữ (xem test kế)."""
    for c in cases:
        prompt = build_scoring_prompt(
            question=c["question"],
            transcript=c["transcript"],
            job_category=c["job_category"],
            criteria=c["criteria"],
            language=c.get("language", "vi"),
            seniority=c["seniority"],
        )
        assert isinstance(prompt, str) and prompt.strip()


def test_moi_ca_seniority_khong_doi_prompt_cham_khi_khe_rong(cases):
    """J5 — bất biến âm quan trọng nhất: khe `scoring_focus` rỗng (registry sạch, đúng trạng thái
    mặc định) ⇒ truyền `seniority` hay không TUYỆT ĐỐI không đổi một byte nào của prompt chấm.
    Đây là điều kiện để đo tách bạch ảnh hưởng J1 (luật công bằng) với J5 (cấp độ) trên dữ liệu
    thật bằng `benchmark-scoring.py`."""
    for c in cases:
        kwargs = dict(
            question=c["question"], transcript=c["transcript"], job_category=c["job_category"],
            criteria=c["criteria"], language=c.get("language", "vi"))
        with_level = build_scoring_prompt(seniority=c["seniority"], **kwargs)
        without_level = build_scoring_prompt(seniority=None, **kwargs)
        assert with_level == without_level, (
            f"ca '{c['case_id']}': prompt chấm đổi dù khe scoring_focus đang rỗng — vi phạm bất "
            "biến âm của J5")


def test_moi_ca_dung_duoc_prompt_khong_seniority(cases):
    """Đối chứng: cùng ca đó nhưng KHÔNG truyền seniority (mô phỏng buổi B2B, van CAMP-10) vẫn
    phải dựng được prompt không lỗi — J5 không được làm hỏng đường B2B."""
    for c in cases:
        prompt = build_scoring_prompt(
            question=c["question"],
            transcript=c["transcript"],
            job_category=c["job_category"],
            criteria=c["criteria"],
            language=c.get("language", "vi"),
        )
        assert isinstance(prompt, str) and prompt.strip()
