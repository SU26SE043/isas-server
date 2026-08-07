"""Chấm bài giảng theo ĐỀ của nó (BC13/D20) + ghép Markdown trả cho InterviewService.

Vì sao có file này — sự cố 2026-08-03 trên deploy: mở bài "Giới thiệu về Business Analyst và
vai trò cốt lõi" nhận về **đúng một dòng tiêu đề**, không có thân bài. Guard lúc đó chỉ chặn chuỗi
rỗng (``if not theory``) nên một dòng tiêu đề vẫn tính là "có nội dung"; lý thuyết lại chỉ sinh MỘT
LẦN rồi lưu, nên người học mở lại vẫn thấy y hệt, vĩnh viễn.

Cách chấm CỐ Ý không đo độ dài và không dò từ khoá — cả hai đều đo sai thứ cần đo. Một bài giảng
đạt là bài **giải thích đủ cái đề của nó**, mà đề thì hệ thống đã biết sẵn: tiêu đề bài học +
``focusCriteria`` của milestone chứa nó. Để kiểm được điều đó bằng máy mà không cần đọc hiểu, ta bắt
mô hình **tự khai mỗi mục phục vụ tiêu chí nào**, rồi kiểm phủ bằng tập hợp — mô hình chỉ khai được
tên trong tập đã cấp, tên tự đặt tự rơi. Cùng một thủ pháp với grounding (chỉ cite được ``chunkId``
trong tập đã cấp, D27) và với allowlist tên miền của F15 (không tin URL mô hình hứa).

Hệ quả có chủ đích: viết dài không giúp qua bài, chỉ viết đủ phần mới qua.
"""

from __future__ import annotations

from app.language import EN, VI, lesson_example_heading, lesson_mistakes_heading, normalize

# Nhãn phần trong Markdown ghép ra. Đặt hằng để test khoá được, và để đổi chữ hiển thị không phải
# đi sửa rải rác. Giá trị lấy TỪ app/language.py để bản tiếng Việt và bản tiếng Anh không trôi khỏi
# nhau (Q10 — trên deploy 2026-08-07 một bài `language=en` có thân bài tiếng Anh nhưng hai mục cuối
# vẫn là "## Ví dụ minh hoạ" / "## Lỗi thường gặp…", vì hai hằng này ghi cứng rồi được nối VÔ ĐIỀU
# KIỆN). `normalize(VI)` không hỏi env (chỉ nhánh EN mới tra allowlist) ⇒ đọc lúc import là tất định.
EXAMPLE_HEADING = lesson_example_heading(VI)
MISTAKES_HEADING = lesson_mistakes_heading(VI)

# Câu chữ khiếm khuyết KHÔNG chỉ để hiển thị: nó được dùng lại NGUYÊN VĂN làm `retry_feedback` nhét
# vào prompt lượt viết lại (`GeminiProvider.generate_lesson_theory`). Bài tiếng Anh mà nhận xét sửa
# bài bằng tiếng Việt = ra đề bằng hai thứ tiếng, nên bảng này phải song ngữ chứ không phải chỉ phần
# render. Giữ dạng bảng (thay vì rải f-string theo nhánh if) để thêm ngôn ngữ thứ ba là thêm cột.
_MESSAGES: dict[str, dict[str, str]] = {
    "unnamed_section": {VI: "(không tên)", EN: "(unnamed)"},
    "empty_sections": {
        VI: "Các mục sau chưa có nội dung (body rỗng): {items}",
        EN: "These sections have no content (empty body): {items}",
    },
    "missing_criteria": {
        VI: ("Chưa có mục nào giải thích các tiêu chí trọng tâm sau: {items}"
             ". Mỗi tiêu chí cần một mục riêng, trường criterion ghi đúng nguyên văn tên tiêu chí."),
        EN: ("No section explains these focus criteria: {items}"
             ". Each criterion needs its own section, and that section's `criterion` field must "
             "repeat the criterion name verbatim."),
    },
    "no_sections": {
        VI: 'Bài học "{title}" chưa có mục nội dung nào (sections rỗng).',
        EN: 'Lesson "{title}" has no content section at all (sections is empty).',
    },
    "no_example": {
        VI: "Thiếu ví dụ minh hoạ cụ thể (example rỗng).",
        EN: "Missing a concrete worked example (example is empty).",
    },
    "no_mistakes": {
        VI: "Thiếu phần lỗi/hiểu lầm thường gặp khi trả lời phỏng vấn (commonMistakes rỗng).",
        EN: ("Missing the section on common interview mistakes/misconceptions "
             "(commonMistakes is empty)."),
    },
    # Dùng ở provider khi lượt trước không parse được — cùng đường đi vào `retry_feedback`.
    "not_json": {
        VI: "Bản trước không phải JSON hợp lệ: {raw}",
        EN: "Your previous answer was not valid JSON: {raw}",
    },
}


def message(key: str, language: str | None, **fmt: object) -> str:
    """Câu chữ khiếm khuyết theo ngôn ngữ bài giảng. Ngôn ngữ lạ → tiếng Việt (fail-safe)."""
    return _MESSAGES[key][normalize(language)].format(**fmt)


def _text(value: object) -> str:
    """Chuỗi đã trim; mọi kiểu khác (None/số/dict do mô hình trả bừa) → rỗng."""
    return value.strip() if isinstance(value, str) else ""


def _norm(name: str) -> str:
    """Khoá so khớp tên tiêu chí: bỏ khoảng trắng thừa + không phân biệt hoa/thường.

    CỐ Ý dừng ở đây, không fuzzy-match: nới lỏng thêm là tự tay mở lại đúng lỗ mà cách kiểm này
    sinh ra để bịt — mô hình đặt tên xấp xỉ rồi vẫn được tính là đã phủ tiêu chí.
    """
    return " ".join(name.split()).casefold()


def _sections(data: dict) -> list[dict]:
    raw = data.get("sections")
    return [s for s in raw if isinstance(s, dict)] if isinstance(raw, list) else []


def evaluate_lesson_theory(data: dict, focus_criteria: list[str] | None,
                           lesson_title: str, *, language: str = VI) -> list[str]:
    """Chấm bài. Trả về danh sách khiếm khuyết — **rỗng nghĩa là đạt**.

    Câu chữ trả về được dùng lại nguyên văn làm nhận xét cho lượt viết lại, nên phải nói rõ thiếu
    gì (mô hình sửa được), không phải "bài không hợp lệ" — và phải cùng ngôn ngữ với bài (Q10).

    Cách chấm KHÔNG đổi theo ngôn ngữ: rubric chỉ kiểm mục có ruột · `criterion` thuộc tập tiêu chí
    do CALLER truyền vào · example/commonMistakes không rỗng. Không chỗ nào khớp theo tiêu đề tiếng
    Việt, nên bài tiếng Anh không vì thế mà trượt.
    """
    defects: list[str] = []
    sections = _sections(data)

    # 1. Mọi mục phải có ruột. Mục rỗng không tính là đã phủ tiêu chí ở bước 2.
    filled: list[dict] = []
    empty_headings: list[str] = []
    for s in sections:
        if _text(s.get("body")):
            filled.append(s)
        else:
            empty_headings.append(
                _text(s.get("heading")) or _text(s.get("criterion"))
                or message("unnamed_section", language))
    if empty_headings:
        defects.append(message("empty_sections", language,
                               items=", ".join(empty_headings)))

    # 2. Phủ đề: mỗi tiêu chí trọng tâm phải có ít nhất một mục CÓ RUỘT giải thích nó.
    wanted = [c for c in (focus_criteria or []) if _text(c)]
    if wanted:
        covered = {_norm(_text(s.get("criterion"))) for s in filled}
        missing = [c for c in wanted if _norm(c) not in covered]
        if missing:
            defects.append(message("missing_criteria", language,
                                   items=", ".join(missing)))
    elif not filled:
        # Milestone không khai tiêu chí → vẫn phải có ít nhất một mục dạy chủ đề bài.
        defects.append(message("no_sections", language, title=lesson_title))

    # 3–4. Hai phần bắt buộc còn lại của một bài giảng.
    if not _text(data.get("example")):
        defects.append(message("no_example", language))
    if not _text(data.get("commonMistakes")):
        defects.append(message("no_mistakes", language))

    return defects


def render_lesson_markdown(lesson_title: str, data: dict, *, language: str = VI) -> str:
    """Ghép cấu trúc đã chấm thành Markdown — hợp đồng với InterviewService/FE KHÔNG đổi.

    Ghép ở server thay vì để mô hình tự trình bày: cùng một lượt vừa kiểm được cấu trúc, vừa cho ra
    định dạng nhất quán giữa mọi bài. ``body`` vẫn là markdown tự do nên bảng/code block/bullet của
    mô hình giữ nguyên.
    """
    parts = [f"# {lesson_title.strip()}"]

    for s in _sections(data):
        body = _text(s.get("body"))
        if not body:
            continue
        # heading mô hình trả về đôi khi đã kèm '#'. Bỏ đi rồi tự áp cấp 2 để không vỡ phân cấp
        # (một '#' cấp 1 thứ hai trong bài sẽ đọc như bài mới).
        heading = _text(s.get("heading")).lstrip("#").strip() or _text(s.get("criterion"))
        parts.append(f"## {heading}\n\n{body}" if heading else body)

    # Hai nhãn này do SERVER ghép, không phải model sinh ⇒ chúng là chỗ duy nhất trong bài mà
    # tiếng Việt lọt được vào bài tiếng Anh (Q10). Phải hỏi ngôn ngữ, không được lấy hằng.
    example = _text(data.get("example"))
    if example:
        parts.append(f"## {lesson_example_heading(language)}\n\n{example}")

    mistakes = _text(data.get("commonMistakes"))
    if mistakes:
        parts.append(f"## {lesson_mistakes_heading(language)}\n\n{mistakes}")

    return "\n\n".join(parts)
