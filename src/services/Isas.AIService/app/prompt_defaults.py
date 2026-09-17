"""Bản MẶC ĐỊNH của từng mảnh prompt admin sửa được (F21) — để màn quản trị HIỆN được thứ đang chạy.

Vì sao cần: `GET /api/admin/prompts` (Interview) trả `body=null` cho khoá chưa ai sửa, và bản mặc định
CỐ Ý chỉ nằm trong mã Python (`DTOs/PromptTemplate.cs`: hai nguồn sự thật cho cùng câu chữ sẽ lệch
nhau ngay lần sửa đầu). Hệ quả đo trên dev 2026-09-16: admin mở một khoá lên thấy ô TRỐNG, không biết
mình sắp thay câu nào. Module này là nguồn DUY NHẤT (đọc từ chính các literal mà builder dùng), AIService
phục vụ qua ``GET /api/v1/prompt-defaults`` (stateless — GEN-4 vẫn nguyên), Interview ghép vào
``defaultBody`` với fail-open (AIService chết ⇒ null, màn vẫn dùng được).

Khe THAY có chuỗi mẫu (có biến ``{role}``/``{job_category}`` — trả nguyên placeholder, admin thấy được
chỗ hệ sẽ điền); khe THÊM mặc định RỖNG (hệ không thêm gì).

⚠ `test_prompt_defaults.py` khoá ĐỒNG BỘ: mỗi khe THAY phải xuất hiện nguyên văn (đã format) trong
prompt do builder dựng. Sửa literal trong builder mà quên đây ⇒ test đỏ — không có test đó thì hai chỗ
trôi nhau và màn admin lại nói dối theo cách khác.
"""

from __future__ import annotations

from app import prompts, seniority

# Khe THAY của prompt sinh/chấm — GIỮ NGUYÊN VĂN dòng trong builder (`build_prompt`,
# `build_scoring_prompt`), chỉ đổi f-string thành placeholder.
QUESTIONS_INTRO_TEMPLATE = "Bạn là một interviewer chuyên nghiệp cho vị trí {role}."
SCORING_PERSONA_TEMPLATE = "Bạn là giám khảo phỏng vấn cho vị trí {job_category}."

# Khe THÊM — mặc định rỗng: hệ KHÔNG nối gì vào prompt.
_APPEND_KEYS = (
    prompts.K_SCORING_EXTRA,
    prompts.K_QUESTIONS_GUIDANCE,
    prompts.K_CRITERION_LEVELS_GUIDANCE,
    prompts.K_CV_ANALYSIS_GUIDANCE,
    prompts.K_JD_REQUIREMENTS_GUIDANCE,
)


def build_defaults() -> dict[str, str]:
    """Bản đồ khoá → văn bản mặc định. Khoá KHÔNG có ở đây = không phải khe admin sửa được."""
    out: dict[str, str] = {
        prompts.K_QUESTIONS_INTRO: QUESTIONS_INTRO_TEMPLATE,
        prompts.K_SCORING_PERSONA: SCORING_PERSONA_TEMPLATE,
        prompts.K_CV_REQUIREMENTS_WORKFLOW: prompts._CV_REQUIREMENTS_WORKFLOW_DEFAULT,
        prompts.K_CV_REQUIREMENTS_LEVEL_RUBRIC: prompts._CV_REQUIREMENTS_LEVEL_RUBRIC_DEFAULT,
    }
    for key in _APPEND_KEYS:
        out[key] = ""
    for category, name in prompts.CATEGORY_NAMES.items():
        out[f"category.{category}.display_name"] = name
        out[f"category.{category}.guidance"] = ""
    for level in seniority.LEVELS:
        out[f"seniority.{level}.profile"] = seniority._PROFILE_DEFAULTS[level]
        out[f"seniority.{level}.scoring_focus"] = ""
        for category in prompts.CATEGORY_NAMES:
            out[f"category.{category}.seniority.{level}.knowledge"] = (
                seniority._KNOWLEDGE_DEFAULTS.get(category, {}).get(level, "")
            )
    return out


PLACEHOLDERS: dict[str, str] = {
    "role": "Tên hiển thị của nghề (category.<nghề>.display_name)",
    "job_category": "Mã nghề đang chấm (BA/BE/FE)",
}
