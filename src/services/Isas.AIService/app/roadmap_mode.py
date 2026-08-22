"""Chế độ lộ trình — ``LevelUp`` (mặc định, hành vi cũ) vs ``Reinforce`` (ôn tập lại).

Vì sao có file này — mọi lộ trình hôm nay đều xây theo hướng TIẾN LÊN một cấp: ``roadmaps.level``
là trình độ MỤC TIÊU và cả prompt lẫn ``seniority_calibration_block`` hiệu chỉnh nội dung theo mức
đó. Không có đường nào để người học nói *"giữ nguyên trình độ, chỉ vá những chỗ tôi hay sai"* —
trong khi dữ liệu để làm đúng việc đó đã nằm sẵn: ``session_criterion_scores`` (điểm yếu ĐO ĐƯỢC)
và ``answer_scores.reasoning`` (trích NGUYÊN VĂN chỗ sai, E11).

``Reinforce`` khác ``LevelUp`` ở ĐÚNG ba chỗ, và cả ba đều là chỉ thị hệ thống chèn vào prompt:
  1. bám ĐIỂM YẾU đo được thay vì bám mức mục tiêu,
  2. GIỮ NGUYÊN trình độ (không nâng cấp độ, không thêm chủ đề của cấp trên),
  3. nghiêng về LÝ THUYẾT giải thích *vì sao lần trước sai*, thay vì mở rộng phạm vi kiến thức.

🔴 Bất biến: nhánh ``LevelUp`` KHÔNG được đổi một byte nào. Mọi câu chữ mới phải nằm sau một
``if`` chỉ đúng với ``Reinforce`` — có test golden khoá điều đó (``test_roadmap_mode.py``).

Câu chữ viết bằng tiếng Việt, KHÔNG song ngữ — khớp ``seniority_calibration_block`` và khối
``weaknesses`` của ``build_lesson_theory_prompt``: đây là chỉ thị hệ thống định hình NỘI DUNG, còn
ngôn ngữ ĐẦU RA đã do ``field_lang(language)`` quy định ở câu dẫn.
"""
from __future__ import annotations

LEVEL_UP = "LevelUp"
REINFORCE = "Reinforce"
DEFAULT_MODE = LEVEL_UP

_MODES: frozenset[str] = frozenset({LEVEL_UP, REINFORCE})


def normalize_mode(value: str | None) -> str:
    """Chuẩn hoá về đúng một khoá của `_MODES`; giá trị lạ/rỗng/None → `DEFAULT_MODE`.

    FAIL-OPEN có chủ đích, mẫu `app.roadmap_quality.normalize_scope` / `app.seniority.normalize`:
    ở tầng này một `mode` gõ sai chỉ nên rơi về hành vi cũ, không nên làm hỏng cả roadmap. Việc
    TỪ CHỐI giá trị lạ là của .NET (`RoadmapService.ValidateMode` → 400) — nơi biết đây là request
    của người dùng thật và trả lời được cho họ; ở đây fail-closed sẽ biến một lỗi gõ thành 502.
    """
    mode = (value or "").strip()
    return mode if mode in _MODES else DEFAULT_MODE


def is_reinforce(mode: str | None) -> bool:
    """`True` khi và chỉ khi mode (đã chuẩn hoá) là `Reinforce`."""
    return normalize_mode(mode) == REINFORCE


def roadmap_headline(mode: str, role: str, level_name: str, output_language: str) -> str:
    """Câu dẫn của `build_roadmap_prompt`.

    ⚠ Nhánh `LevelUp` phải trả về CHUỖI Y HỆT bản trước khi có chế độ ôn tập — đây là chỗ dễ làm
    vỡ bất biến golden nhất, vì nó nằm ngay câu đầu tiên của mọi prompt roadmap.
    """
    if is_reinforce(mode):
        return (
            f"Xây dựng ROADMAP ÔN TẬP LẠI gồm nhiều MILESTONE cho vị trí {role}, "
            f"GIỮ NGUYÊN trình độ hiện tại {level_name} (KHÔNG nâng lên cấp cao hơn), "
            f"bằng {output_language}."
        )
    return (
        f"Xây dựng ROADMAP ôn tập gồm nhiều MILESTONE cho vị trí {role}, "
        f"trình độ mục tiêu {level_name}, bằng {output_language}."
    )


def roadmap_mode_block(mode: str, level_name: str) -> str | None:
    """Khối chỉ thị chế độ cho `build_roadmap_prompt`; `None` với `LevelUp` (không chèn gì).

    Đặt cùng chỗ với `seniority_calibration_block` — SAU khối cấu trúc bắt buộc, TRƯỚC khối chống
    prompt-injection và trước mọi DỮ LIỆU ứng viên: đây là chỉ thị hợp lệ của hệ thống, không được
    để lẫn thứ tự với phần dữ liệu đứng sau (cùng lý do đã ghi ở `build_prompt`/BE-3).
    """
    if not is_reinforce(mode):
        return None
    return (
        "CHẾ ĐỘ ÔN TẬP (REINFORCE) — chỉ thị hệ thống, ưu tiên cao hơn mọi gợi ý khác:\n"
        f"1. GIỮ NGUYÊN trình độ {level_name}. KHÔNG nâng độ khó lên cấp cao hơn, KHÔNG thêm chủ "
        "đề vốn thuộc cấp trên. Mục tiêu là VÁ chỗ còn hổng ở đúng tầm hiện tại, không phải đi lên "
        "một bậc.\n"
        "2. Toàn bộ milestone phải nhắm vào các ĐIỂM YẾU ĐÃ ĐO ĐƯỢC nêu bên dưới. KHÔNG mở rộng "
        "sang năng lực mới mà dữ liệu không cho thấy ứng viên đang yếu.\n"
        "3. Mỗi lesson là một bài LÝ THUYẾT giải thích ĐÚNG chỗ ứng viên đã sai: vì sao câu trả "
        "lời trước chưa đạt, hiểu sai khái niệm nào, cần trình bày thế nào cho đúng. KHÔNG đặt "
        "lesson theo kiểu giới thiệu chủ đề mới hay mở rộng phạm vi kiến thức."
    )


def lesson_mode_block(mode: str, level_name: str) -> str | None:
    """Khối chỉ thị chế độ cho `build_lesson_theory_prompt`; `None` với `LevelUp`.

    🔴 CỐ Ý chỉ đổi TRỌNG TÂM, KHÔNG đụng 3 phần bắt buộc (`sections` / `example` /
    `commonMistakes`): `app.lesson_quality.evaluate_lesson_theory` chấm bài theo ĐÚNG cấu trúc đó
    và không fuzzy-match. Ra đề một đằng chấm một nẻo thì mô hình trượt vì lý do nó không được
    biết — hết lượt viết lại là `generate_lesson_theory` raise ⇒ InterviewService trả **502** ⇒
    người học KHÔNG MỞ ĐƯỢC bài. Vì vậy khối này nói rõ "giữ đủ 3 phần" ngay câu đầu.
    """
    if not is_reinforce(mode):
        return None
    return (
        "CHẾ ĐỘ ÔN TẬP (REINFORCE) — GIỮ ĐỦ 3 phần bắt buộc nêu trên, chỉ đổi TRỌNG TÂM:\n"
        f"- Giữ nguyên trình độ {level_name}, KHÔNG nâng độ khó.\n"
        "- sections: giải thích vì sao cách trả lời trước của ứng viên chưa đạt ở đúng tiêu chí "
        "đó, rồi chỉ ra cách trình bày đúng. KHÔNG mở rộng sang kiến thức mới nằm ngoài chỗ đã sai.\n"
        "- commonMistakes: ưu tiên chính những lỗi ứng viên đã mắc theo bằng chứng bên dưới "
        "(nếu có), thay vì lỗi chung chung của mọi người."
    )
