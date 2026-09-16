"""F21 — bản mặc định phục vụ màn admin phải ĐỒNG BỘ với thứ builder thật đang ghép vào prompt.

Không có test này thì `prompt_defaults.py` và `prompts.py`/`seniority.py` trôi nhau mà không gì kêu —
màn admin lại hiện một "bản mặc định" không phải bản đang chạy (đúng cách nói dối cũ, đổi hình dạng).
"""
from fastapi.testclient import TestClient

from app import main as main_module
from app import prompt_registry, prompts, seniority
from app.prompt_defaults import PLACEHOLDERS, build_defaults

client = TestClient(main_module.app)


def setup_function():
    prompt_registry.reset_cache()


def test_khe_thay_xuat_hien_nguyen_van_trong_prompt_that():
    d = build_defaults()
    # Sinh câu hỏi: intro thay {role} bằng tên nghề hiển thị.
    q = prompts.build_prompt("BE", None, None, 5)
    assert d["questions.intro"].format(role=prompts.CATEGORY_NAMES["BE"]) in q
    # Chấm: persona thay {job_category} bằng mã nghề.
    s = prompts.build_scoring_prompt("Q?", "A.", "BE", [{"id": "c1", "name": "Giao tiếp", "weight": 1, "maxScore": 5}])
    assert d["scoring.persona"].format(job_category="BE") in s
    # Tên nghề hiển thị.
    for cat, name in prompts.CATEGORY_NAMES.items():
        assert d[f"category.{cat}.display_name"] == name == prompts.category_display_name(cat)
    # Cấp độ: dòng profile mặc định nằm trong khối hiệu chỉnh.
    block = seniority.calibration_block("Senior", "BE")
    for level in seniority.LEVELS:
        assert d[f"seniority.{level}.profile"] in block
    # Kiến thức nghề×mức: mặc định đúng dict seed.
    assert d["category.BE.seniority.Senior.knowledge"] == seniority._KNOWLEDGE_DEFAULTS["BE"]["Senior"]


def test_khe_them_mac_dinh_rong():
    d = build_defaults()
    for key in ("scoring.extra_guidance", "questions.guidance", "criterion_levels.guidance",
                "cv_analysis.guidance", "jd_requirements.guidance", "category.FE.guidance",
                "seniority.Junior.scoring_focus"):
        assert d[key] == "", key


def test_moi_khoa_ma_builder_doc_deu_co_mac_dinh():
    """Mọi khoá `prompt_registry.get(K, default)` trong builder phải có trong bản đồ — thêm khoá mà
    quên khai ở đây thì màn admin thấy ô trống trở lại đúng cho khoá đó."""
    d = build_defaults()
    for key in ("scoring.persona", "scoring.extra_guidance", "questions.intro", "questions.guidance",
                "criterion_levels.guidance", "cv_analysis.guidance", "cv_requirements.workflow",
                "cv_requirements.level_rubric", "jd_requirements.guidance"):
        assert key in d, key
    assert len([k for k in d if k.startswith("category.") and k.endswith(".display_name")]) == 3
    assert len([k for k in d if k.endswith(".knowledge")]) == 12


def test_endpoint_gate_token_va_tra_ban_do(monkeypatch):
    monkeypatch.setattr(main_module.settings, "internal_token", "secret")
    assert client.get("/api/v1/prompt-defaults").status_code == 401
    r = client.get("/api/v1/prompt-defaults", headers={"X-Internal-Token": "secret"})
    assert r.status_code == 200
    body = r.json()
    assert body["defaults"]["questions.intro"] == "Bạn là một interviewer chuyên nghiệp cho vị trí {role}."
    assert body["defaults"]["seniority.Senior.profile"].startswith("Senior:")
    assert set(body["placeholders"]) == set(PLACEHOLDERS)
