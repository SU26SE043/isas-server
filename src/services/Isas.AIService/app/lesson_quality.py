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

# Nhãn phần trong Markdown ghép ra. Đặt hằng để test khoá được, và để đổi chữ hiển thị không phải
# đi sửa rải rác.
EXAMPLE_HEADING = "Ví dụ minh hoạ"
MISTAKES_HEADING = "Lỗi thường gặp khi trả lời phỏng vấn"


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
                           lesson_title: str) -> list[str]:
    """Chấm bài. Trả về danh sách khiếm khuyết — **rỗng nghĩa là đạt**.

    Câu chữ trả về được dùng lại nguyên văn làm nhận xét cho lượt viết lại, nên phải nói rõ thiếu
    gì (mô hình sửa được), không phải "bài không hợp lệ".
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
            empty_headings.append(_text(s.get("heading")) or _text(s.get("criterion")) or "(không tên)")
    if empty_headings:
        defects.append(
            "Các mục sau chưa có nội dung (body rỗng): " + ", ".join(empty_headings))

    # 2. Phủ đề: mỗi tiêu chí trọng tâm phải có ít nhất một mục CÓ RUỘT giải thích nó.
    wanted = [c for c in (focus_criteria or []) if _text(c)]
    if wanted:
        covered = {_norm(_text(s.get("criterion"))) for s in filled}
        missing = [c for c in wanted if _norm(c) not in covered]
        if missing:
            defects.append(
                "Chưa có mục nào giải thích các tiêu chí trọng tâm sau: "
                + ", ".join(missing)
                + ". Mỗi tiêu chí cần một mục riêng, trường criterion ghi đúng nguyên văn tên tiêu chí.")
    elif not filled:
        # Milestone không khai tiêu chí → vẫn phải có ít nhất một mục dạy chủ đề bài.
        defects.append(
            f'Bài học "{lesson_title}" chưa có mục nội dung nào (sections rỗng).')

    # 3–4. Hai phần bắt buộc còn lại của một bài giảng.
    if not _text(data.get("example")):
        defects.append("Thiếu ví dụ minh hoạ cụ thể (example rỗng).")
    if not _text(data.get("commonMistakes")):
        defects.append(
            "Thiếu phần lỗi/hiểu lầm thường gặp khi trả lời phỏng vấn (commonMistakes rỗng).")

    return defects


def render_lesson_markdown(lesson_title: str, data: dict) -> str:
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

    example = _text(data.get("example"))
    if example:
        parts.append(f"## {EXAMPLE_HEADING}\n\n{example}")

    mistakes = _text(data.get("commonMistakes"))
    if mistakes:
        parts.append(f"## {MISTAKES_HEADING}\n\n{mistakes}")

    return "\n\n".join(parts)
