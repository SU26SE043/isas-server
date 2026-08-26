"""BE-1 — kiểm phủ tên tiêu chí cho `focusCriteria` của milestone roadmap; BE-4 — độ dài roadmap
theo SCOPE candidate chọn (§cuối file).

Vì sao có file này — đo trên toàn bộ production: chỉ 25/359 (7%) tên `focusCriteria` khớp tên
tiêu chí THẬT trong rubric (BA 13/195 · BE 12/151 · FE 0/13). Model tự bịa tên vì
`build_roadmap_prompt` trước đây KHÔNG hề nhận danh sách tiêu chí — chỉ có `weaknesses` (tên tiêu
chí YẾU, suy từ baseline buổi luyện cũ), mà 86% roadmap không có baseline nên nhánh else chỉ nói
chung chung "xây roadmap chuẩn cho năng lực cốt lõi", không đưa một cái tên nào để chọn.

Hệ quả downstream: `RoadmapLessonService.BuildWeaknesses` (phía .NET) giao baseline (tên thật) với
`focusCriteria` (tên bịa) ra tập RỖNG — bài học không bao giờ nhận đúng điểm yếu của người học,
KHÔNG lỗi, KHÔNG log, âm thầm.

Cách sửa: `build_roadmap_prompt` liệt kê đủ tên tiêu chí + bắt chọn NGUYÊN VĂN; sau khi model trả
lời, hàm ở đây LỌC BỎ tên không thuộc tập đã cấp — chống bịa BY-CONSTRUCTION, cùng thủ pháp RAG
(chỉ cite `chunkId` đã cấp) và chấm-theo-phạm-vi (chỉ nhận `criterionId` đã gửi). CỐ Ý KHÔNG
fuzzy-match — chỉ chuẩn hoá khoảng trắng + hoa/thường (mẫu `app.lesson_quality._norm`); nới hơn là
mở lại đúng lỗ đang bịt (tên xấp xỉ vẫn được tính là "đã chọn đúng").
"""
from __future__ import annotations

from app.language import EN, VI, normalize

_MESSAGES: dict[str, dict[str, str]] = {
    "milestone_no_criteria": {
        VI: ("Các milestone sau không còn tiêu chí hợp lệ nào sau khi lọc (focusCriteria trước đó "
             "không khớp tên nào trong danh sách tiêu chí đã cho): {titles}. CHỈ được chọn tên "
             "trong danh sách tiêu chí, SAO CHÉP NGUYÊN VĂN — không viết tắt, không dịch, không tự "
             "đặt tên mới. Danh sách tiêu chí hợp lệ: {allowed}"),
        EN: ("These milestones ended up with no valid criteria after filtering (their focusCriteria "
             "did not match any name in the given criteria list): {titles}. You may ONLY pick names "
             "from the criteria list, copied VERBATIM — no abbreviations, no translation, no new "
             "names. Allowed criteria: {allowed}"),
    },
    # MIS1-B2 — không kèm danh sách id hợp lệ (khác milestone_no_criteria): mistake_block đã liệt
    # kê đủ nội dung + id ngay trong prompt gửi lại (mistakes chuyền qua lời gọi đệ quy), nhồi
    # thêm một danh sách id trần (không kèm ngữ cảnh câu hỏi/lý do) vào feedback chỉ làm rối.
    "milestone_no_mistakes": {
        VI: ("Các milestone sau không gom được lỗi nào sau khi lọc (mistakeIds trước đó không khớp "
             "id nào trong danh sách LỖI CỦA ỨNG VIÊN đã cho): {titles}. Mỗi milestone PHẢI là một "
             "chủ đề rút ra từ ÍT NHẤT một lỗi ở khối LỖI CỦA ỨNG VIÊN — CHỈ được dùng id có trong "
             "khối đó, SAO CHÉP NGUYÊN VĂN, TUYỆT ĐỐI không bịa id mới. Không gom được lỗi nào cho "
             "một chủ đề thì ĐỪNG tạo milestone đó."),
        EN: ("These milestones did not gather any mistake after filtering (their mistakeIds did not "
             "match any id in the given CANDIDATE MISTAKES list): {titles}. Every milestone MUST be "
             "a theme drawn from AT LEAST one mistake in that block — you may ONLY use ids from it, "
             "copied VERBATIM, NEVER invent new ones. If you cannot gather any mistake for a theme, "
             "do NOT create that milestone."),
    },
}


# ══════════════════════════════════════════════════════════════════════════════════════════════
# BE-4 — SCOPE (độ dài roadmap do candidate chọn)
#
# Prompt cũ hard-code "3-5 milestone, 2-4 lesson" — mơ hồ (model tự diễn giải "hợp lý" theo cách
# riêng) và KHÔNG có tầng nào cho candidate chọn ngắn/dài. Đo trên production: trung bình THẬT
# 14,1 lesson/roadmap — xa hẳn dải "hợp lý" cũ. Hai preset đóng khung tường minh:
#   Quick     2 milestone × 2 lesson  = 4 lesson  (xem trước nhanh)
#   Standard  4 milestone × 3 lesson  = 12 lesson (mặc định — giữ hành vi client cũ chưa gửi scope)
#
# Model vẫn có thể lờ chỉ thị (giống ca focusCriteria bịa tên ở BE-1) — validate SAU khi model trả
# lời, cắt CỨNG theo trần, KHÔNG raise: một roadmap dài hơn cam kết một chút vẫn dùng được, và tạo
# roadmap KHÔNG trừ credit (D7/D15) nên biến việc "AI lỡ tay sinh dư" thành lỗi 502 là đắt hơn
# nhiều so với âm thầm cắt bớt.
_SCOPES: dict[str, tuple[int, int]] = {
    "Quick": (2, 2),
    "Standard": (4, 3),
}
DEFAULT_SCOPE = "Standard"


def normalize_scope(value: str | None) -> str:
    """Chuẩn hoá về đúng một khoá của `_SCOPES`; giá trị lạ/rỗng/None → `DEFAULT_SCOPE`.

    FAIL-OPEN có chủ đích, mẫu `app.seniority.normalize`: endpoint `/generate-roadmap` bọc mọi lỗi
    thành 502, và roadmap KHÔNG trừ credit — một `scope` gõ sai không đáng làm hỏng cả roadmap.
    """
    scope = (value or "").strip()
    return scope if scope in _SCOPES else DEFAULT_SCOPE


def scope_counts(scope: str) -> tuple[int, int]:
    """(số milestone tối đa, số lesson tối đa MỖI milestone) — tự chuẩn hoá `scope` trước khi tra."""
    return _SCOPES[normalize_scope(scope)]


def scope_instruction(scope: str) -> str:
    """Câu chỉ thị TƯỜNG MINH thay cho "số lượng hợp lý (3-5)" mơ hồ cũ — nêu đúng con số để model
    bám theo, và để bước cắt sau đó (`truncate_to_scope`) hiếm khi phải chạm tới."""
    milestones, lessons = scope_counts(scope)
    return (
        f"Tạo ĐÚNG {milestones} milestone, MỖI milestone ĐÚNG {lessons} lesson "
        f"(tổng {milestones * lessons} lesson). KHÔNG tạo nhiều hơn hay ít hơn."
    )


def truncate_to_scope(
    milestones: list[dict], scope: str,
) -> tuple[list[dict], int, dict[str, int]]:
    """Cắt CỨNG milestones/lessons về đúng trần của `scope`. KHÔNG raise dù model vượt trần.

    Trả về ``(milestones đã cắt, số milestone bị bỏ, {title milestone còn giữ: số lesson bị bỏ})``
    — caller (``GeminiProvider.generate_roadmap``) tự log warning bằng thông tin này, mẫu
    ``filter_milestone_criteria`` (hàm thuần, không log; log là việc của caller biết ngữ cảnh gọi).

    Cắt TỪ ĐUÔI (giữ N phần tử ĐẦU), không cắt đầu: thứ tự milestone/lesson MANG Ý NGHĨA (nền tảng
    trước, nâng cao sau — xem `build_roadmap_prompt`, "đi từ cơ bản đến nâng cao"). Với scope Quick,
    phần nền tảng chính là phần CẦN SỐNG SÓT nhất; cắt đầu sẽ giữ lại phần nâng cao mà bỏ mất nền
    tảng — sai hướng hoàn toàn so với ý định của một roadmap "xem nhanh".
    """
    max_milestones, max_lessons = scope_counts(scope)

    dropped_milestones = max(0, len(milestones) - max_milestones)
    kept = milestones[:max_milestones]

    dropped_lessons: dict[str, int] = {}
    truncated: list[dict] = []
    for m in kept:
        lessons = m.get("lessons") or []
        extra = len(lessons) - max_lessons
        if extra > 0:
            title = str(m.get("title", ""))
            dropped_lessons[title] = dropped_lessons.get(title, 0) + extra
            m = {**m, "lessons": lessons[:max_lessons]}
        truncated.append(m)

    return truncated, dropped_milestones, dropped_lessons


def message(key: str, language: str | None, **fmt: object) -> str:
    """Câu chữ khiếm khuyết theo ngôn ngữ roadmap. Ngôn ngữ lạ → tiếng Việt (fail-safe)."""
    return _MESSAGES[key][normalize(language)].format(**fmt)


def _norm(name: str) -> str:
    """Khoá so khớp tên tiêu chí: bỏ khoảng trắng thừa + không phân biệt hoa/thường.

    CỐ Ý dừng ở đây, không fuzzy-match — mẫu `app.lesson_quality._norm`.
    """
    return " ".join(name.split()).casefold()


def filter_milestone_criteria(
    milestones: list[dict], known_names: list[str],
) -> tuple[list[dict], list[str]]:
    """Lọc `focusCriteria` từng milestone về đúng tập `known_names`.

    Trả về ``(milestones đã lọc, tên các milestone bị RỖNG SAU LỌC dù TRƯỚC lọc không rỗng)``.

    Milestone mà model tự để ``focusCriteria: []`` ngay từ đầu (không liên quan tiêu chí năng lực
    cụ thể nào) KHÔNG bị tính là khiếm khuyết — chỉ milestone CÓ gắn nhãn nhưng toàn bộ nhãn đó là
    tên bịa (không khớp bất kỳ tên nào đã cấp) mới đáng retry.

    ``known_names`` rỗng ⇒ không lọc gì (giữ nguyên hành vi cũ — caller không có gì để đối chiếu).
    """
    if not known_names:
        return milestones, []
    # Ánh xạ dạng-đã-chuẩn-hoá → TÊN CHUẨN (nguyên văn trong rubric), không phải một tập tên.
    #
    # 🔴 Vì sao phải trả TÊN CHUẨN chứ không phải chữ model gõ: phép khớp ở đây bỏ qua hoa/thường
    # (`_norm` = trim + casefold), nên `"phân tích yêu cầu"` được nhận là hợp lệ. Nhưng nếu ta LƯU
    # lại đúng chữ model trả thì downstream vỡ ở chỗ KHÁC, và vỡ IM LẶNG:
    #   RoadmapService persist nguyên văn `focusCriteria`
    #     → baseline là Dictionary<string,decimal> keyed bằng `SessionCriterionScore.CriterionName`
    #       (tên CHUẨN, hoa/thường đúng như rubric)
    #     → RoadmapLessonService.BuildWeaknesses gọi `baseline.TryGetValue(name)` — khớp CHÍNH XÁC
    #   ⇒ `"phân tích yêu cầu"` không bao giờ tìm thấy `"Phân tích yêu cầu"` ⇒ giao rỗng
    #   ⇒ bài giảng KHÔNG nhận được điểm yếu — đúng con bug BE-1 sinh ra để diệt, chỉ thu hẹp lại
    #      thành "tên lệch hoa/thường" thay vì "mọi tên bịa".
    #
    # Chuẩn hoá tại ĐÂY (biên nhận dữ liệu từ model) chứ không phải ở chỗ đọc, vì chỗ đọc có nhiều
    # call site còn biên nhận chỉ có một.
    canonical: dict[str, str] = {}
    for n in known_names:
        name = n.strip()
        if name:
            canonical.setdefault(_norm(name), name)
    if not canonical:
        return milestones, []

    empty_after_filter: list[str] = []
    filtered: list[dict] = []
    for m in milestones:
        original = m.get("focusCriteria") or []
        kept = [canonical[k] for f in original if (k := _norm(f)) in canonical]
        if original and not kept:
            empty_after_filter.append(str(m.get("title", "")))
        filtered.append({**m, "focusCriteria": kept})
    return filtered, empty_after_filter


def filter_milestone_mistakes(
    milestones: list[dict], known_ids: list[str],
) -> tuple[list[dict], list[str]]:
    """MIS1-B2 — lọc `mistakeIds` từng milestone (và mỗi lesson bên trong) về đúng tập `known_ids`.

    Trả về ``(milestones đã lọc, tên các milestone RỖNG SAU LỌC)``.

    Khớp CHÍNH XÁC — id ở đây do .NET MINT (không phải chữ tự do model gõ như tên tiêu chí), nên
    KHÔNG casefold, KHÔNG fuzzy: nới khớp gần đúng là mở lại đúng lỗ mà cơ chế này sinh ra để bịt
    (mẫu `filter_milestone_criteria`, `citedChunkId` của grounding).

    ⚠ NGỮ NGHĨA KHÁC `filter_milestone_criteria` — CỐ Ý không có vế "if original and not kept":
    milestone không nhắm riêng tiêu chí nào (focusCriteria rỗng) vẫn là milestone hợp lệ, nhưng
    milestone GOM KHÔNG ĐƯỢC LỖI NÀO là vô nghĩa với luật gom chủ đề TỪ LỖI (mỗi milestone PHẢI
    rút ra từ ít nhất một lỗi thật). Nên ở đây `mistakeIds` rỗng NGAY TỪ ĐẦU (model không gán
    milestone này cho lỗi nào) CŨNG là khiếm khuyết — y hệt trường hợp toàn bộ id bị lọc vì bịa.

    Lọc CẢ lesson-level `mistakeIds` (không chỉ milestone) — chỉ thị "GOM CHỦ ĐỀ TỪ LỖI" đòi
    ``lessons[].mistakeIds`` là tập con của milestone chứa nó; id lạ lọt qua ở tầng lesson thì
    lời hứa "id không bịa được" của cả cơ chế chỉ đúng một nửa. Lesson rỗng sau lọc KHÔNG tính là
    khiếm khuyết (mistakeIds ở lesson là bổ sung tuỳ chọn — chỉ milestone mới bắt buộc).

    ``known_ids`` rỗng ⇒ không lọc gì (giữ nguyên hành vi cũ — caller không có gì để đối chiếu).
    """
    if not known_ids:
        return milestones, []

    allowed = set(known_ids)

    empty_after_filter: list[str] = []
    filtered: list[dict] = []
    for m in milestones:
        original = m.get("mistakeIds") or []
        kept = [i for i in original if i in allowed]
        if not kept:
            empty_after_filter.append(str(m.get("title", "")))

        filtered_lessons: list[dict] = []
        for lesson in (m.get("lessons") or []):
            lesson_ids = lesson.get("mistakeIds") or []
            filtered_lessons.append(
                {**lesson, "mistakeIds": [i for i in lesson_ids if i in allowed]})

        filtered.append({**m, "mistakeIds": kept, "lessons": filtered_lessons})
    return filtered, empty_after_filter
