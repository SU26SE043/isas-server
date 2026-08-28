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
    """Câu dẫn của `build_roadmap_prompt` — câu ĐẦU TIÊN của mọi prompt roadmap.

    🔴 MIS1-B2 — nhánh `LevelUp` KHÔNG còn giữ nguyên xi: bản cũ nói "trình độ MỤC TIÊU
    {level_name}", coi `level` là ĐÍCH ĐẾN ứng viên muốn tới. Từ MIS1-B2, roadmap được gom TỪ LỖI
    THẬT (xem `build_mistake_block`/khối GOM CHỦ ĐỀ TỪ LỖI trong `build_roadmap_prompt`), không
    còn "chế độ giáo trình" đi lên một cấp nữa — và frontend nay gửi TRÌNH ĐỘ HIỆN TẠI vào field
    `level`, nên câu cũ sẽ ra lệnh sai nghĩa "trình độ mục tiêu <mức người học đang ở>". Câu mới
    nói về ĐỘ KHÓ (khớp đúng việc `level` thật sự làm — hiệu chỉnh qua
    `app.seniority.calibration_block`), không nói về đích đến. Golden hash
    `tests/test_roadmap_mode.py` đã ghi lại theo đúng thay đổi này — ngoại lệ DUY NHẤT cho phép sửa
    golden ở bước MIS1-B2.

    Nhánh `Reinforce` KHÔNG đổi — nó vốn đã nói đúng "trình độ HIỆN TẠI", không mâu thuẫn với
    field `level` mang nghĩa mới.
    """
    if is_reinforce(mode):
        return (
            f"Xây dựng ROADMAP ÔN TẬP LẠI gồm nhiều MILESTONE cho vị trí {role}, "
            f"GIỮ NGUYÊN trình độ hiện tại {level_name} (KHÔNG nâng lên cấp cao hơn), "
            f"bằng {output_language}."
        )
    return (
        f"Xây dựng ROADMAP ôn tập gồm nhiều MILESTONE cho vị trí {role}, "
        f"độ khó tương ứng trình độ {level_name}, bằng {output_language}."
    )


def roadmap_mode_block(
    mode: str, level_name: str, *, has_weaknesses: bool = False
) -> str | None:
    """Khối chỉ thị chế độ cho `build_roadmap_prompt`.

    🔴 MIS1-B2 — KHÔNG còn được gọi từ `build_roadmap_prompt`: nhánh `LevelUp` "nửa sau nâng lên
    trình độ mục tiêu" mâu thuẫn thẳng với luật gom chủ đề TỪ LỖI (mọi milestone phải rút ra từ
    lỗi thật, không phải nửa-nâng-cấp-tự-do), và đây CHÍNH LÀ chế độ "giáo trình" mà MIS1-B2 gỡ bỏ.
    Giữ lại định nghĩa (không xoá) để không mất lịch sử/khả năng tái dùng, nhưng hiện KHÔNG có
    caller nào trong `app/`.

    Đặt cùng chỗ với `seniority_calibration_block` — SAU khối cấu trúc bắt buộc, TRƯỚC khối chống
    prompt-injection và trước mọi DỮ LIỆU ứng viên: đây là chỉ thị hợp lệ của hệ thống, không được
    để lẫn thứ tự với phần dữ liệu đứng sau (cùng lý do đã ghi ở `build_prompt`/BE-3).

    Ba ca:

    * ``Reinforce`` → khối ôn tập thuần (giữ nguyên trình độ, chỉ vá chỗ hổng).
    * ``LevelUp`` + **có** điểm yếu → khối TRỘN ĐÔI: nửa đầu số chặng sửa lỗi đo được, nửa sau
      nâng lên trình độ mục tiêu. Một lộ trình thật phải làm cả hai — người dùng không nên phải
      chọn giữa "sửa cái đang sai" và "tiến lên".
    * ``LevelUp`` + **không** điểm yếu → ``None``, prompt không đổi một byte so với trước khi có
      chế độ nào. Đây là đường của người chưa chọn buổi luyện nào.

    ⚠ Chia theo **số CHẶNG**, không theo số bài: `app.roadmap_quality.truncate_to_scope` cắt theo
    chặng *và* theo bài-mỗi-chặng, nên chia theo bài sẽ đá nhau với nó.
    """
    if not is_reinforce(mode):
        if not has_weaknesses:
            return None
        return (
            "PHÂN BỔ LỘ TRÌNH — chỉ thị hệ thống, ưu tiên cao hơn mọi gợi ý khác:\n"
            "1. Chia số MILESTONE làm hai nửa. NỬA ĐẦU (các milestone đầu tiên) dành cho việc SỬA "
            "những ĐIỂM YẾU ĐÃ ĐO ĐƯỢC nêu bên dưới — mỗi milestone nửa này phải nhắm đúng tên "
            "tiêu chí trong khối ĐIỂM YẾU, không mở rộng sang chủ đề khác.\n"
            f"2. NỬA SAU dành cho việc NÂNG lên trình độ mục tiêu {level_name}: chủ đề mới, độ khó "
            "cao hơn, theo đúng khối hiệu chỉnh cấp độ ở trên.\n"
            "3. Số milestone lẻ thì nửa đầu (sửa lỗi) được nhiều hơn một — chỗ đang sai quan trọng "
            "hơn chỗ chưa tới."
        )
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
