# tests/test_seniority_calibration_knowledge_split_rec1_b3.py — REC1-B3: tách khối KIẾN THỨC
# (nội dung thật, 290-439 ký tự, phủ 12/12 ô nghề×mức) ra khỏi khối HIỆU CHỈNH CẤP ĐỘ (trần/sàn
# viết cho prompt SINH CÂU HỎI — mở đầu "Toàn bộ CÂU HỎI phải được hiệu chỉnh…", kết bằng "KHÔNG
# ĐƯỢC VƯỢT LÊN CẤP CAO HƠN"). Bài GIẢNG không sinh câu hỏi nào nên bị khối đó bó sai chỗ.
#
# Hai bất biến phải giữ, đúng thứ tự ưu tiên:
#   (1) `calibration_block` (dùng cho build_prompt/build_criterion_levels_prompt/build_roadmap_prompt
#       — CẤM đụng ở REC1-B3) ra chuỗi Y HỆT trước/sau khi tách `knowledge_block` ra làm hàm riêng.
#       Đối chứng DƯƠNG bắt buộc — job_category="BE" mức "Senior" CÓ knowledge thật trong nguồn —
#       để phép so sánh không "pass" giả vì cả hai vế đều rỗng.
#   (2) Prompt BÀI GIẢNG (build_lesson_theory_prompt) THÔI mang câu "không được vượt lên cấp cao
#       hơn" (vô nghĩa với một đề bài không sinh câu hỏi) nhưng VẪN mang đúng nội dung kiến thức.
#
# Không gọi Gemini thật.
from app import prompt_registry
from app import seniority as seniority_module
from app.prompts import build_lesson_theory_prompt


# ══════════════════════════════════════════════════════════════════════════════
# (1) calibration_block byte-identical trước/sau khi tách knowledge_block
# ══════════════════════════════════════════════════════════════════════════════

# Sao y NGUYÊN VĂN cấu trúc chuỗi của `calibration_block` — mẫu
# `test_seniority_registry_j4.py::_OLD_CALIBRATION_BLOCK_TEMPLATE`. Đây là phần KHÔNG được đổi.
_CALIBRATION_HEADER_TEMPLATE = (
    "CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: {level}\n"
    "Toàn bộ câu hỏi PHẢI được hiệu chỉnh ĐÚNG TẦM cấp độ này — đây là lựa chọn tường minh của "
    "người dùng, không phải gợi ý:\n"
    "- Fresher: kiến thức nền tảng, khái niệm cốt lõi, tình huống đơn giản một bước. KHÔNG hỏi "
    "vận hành hệ thống quy mô lớn, KHÔNG hỏi đánh đổi kiến trúc.\n"
    "- Junior: áp dụng kiến thức vào công việc hằng ngày, gỡ lỗi thường gặp, quy trình làm việc "
    "nhóm; nghiêng về 'làm thế nào' hơn là 'vì sao chọn phương án này'.\n"
    "- Middle: thiết kế module, so sánh đánh đổi giữa các phương án, tối ưu hiệu năng, xử lý ca "
    "biên, rút kinh nghiệm từ dự án thật.\n"
    "- Senior: đánh đổi kiến trúc ở quy mô hệ thống, vận hành và độ tin cậy, chuẩn hoá kỹ thuật, "
    "ra quyết định dưới ràng buộc thực tế, dẫn dắt và định hướng người khác.\n"
    "CHỈ áp dụng dòng ứng với cấp độ {level}; bỏ qua các dòng còn lại. Thang 'đi từ cơ bản đến "
    "nâng cao' nêu trên áp dụng TRONG phạm vi cấp độ {level}, không được vượt lên cấp cao hơn "
    "hay tụt xuống cấp thấp hơn."
)

# Nguyên văn dòng kiến thức mặc định của (BE, Senior) trong `_KNOWLEDGE_DEFAULTS` — copy tay từ
# `app/seniority.py`, KHÔNG import dict private (mẫu `test_seniority_be3_knowledge_ba.py`: nếu
# một ngày nội dung BE/Senior đổi mà quên cập nhật đây, test đỏ đúng lúc cần biết).
_BE_SENIOR_KNOWLEDGE = (
    "Kiến thức BE mức Senior: thiết kế kiến trúc hệ thống nhiều service (đồng bộ dữ liệu, "
    "idempotency, retry/backoff), đánh giá đánh đổi giữa các mô hình lưu trữ ở quy mô lớn, "
    "đảm bảo độ tin cậy/khả năng mở rộng, dẫn dắt kỹ thuật cho team. Câu hỏi nên xoay "
    "quanh cách xử lý sự cố sản xuất, thiết kế hệ thống chịu lỗi, và đánh đổi giữa chi phí "
    "vận hành với độ phức tạp kỹ thuật."
)


def test_calibration_block_khong_job_category_byte_identical_sau_khi_tach():
    """Đối chứng ÂM (không nên có gì thêm) — nhánh này KHÔNG chạm `knowledge_block` nội bộ vì
    `job_category=None` chặn từ đầu `calibration_block`."""
    expected = _CALIBRATION_HEADER_TEMPLATE.format(level="Senior")
    assert seniority_module.calibration_block("Senior") == expected


def test_calibration_block_co_knowledge_that_byte_identical_sau_khi_tach():
    """ĐỐI CHỨNG DƯƠNG bắt buộc (task REC1-B3): (BE, Senior) CÓ knowledge thật trong nguồn —
    thiếu ca này thì so hai chuỗi RỖNG với nhau cũng "pass" mà không chứng minh được gì.
    """
    expected = _CALIBRATION_HEADER_TEMPLATE.format(level="Senior") + "\n" + _BE_SENIOR_KNOWLEDGE
    block = seniority_module.calibration_block("Senior", "BE")
    assert block == expected
    # Khẳng định lại đối chứng dương THẬT SỰ có nội dung (không phải hai chuỗi rỗng trùng nhau).
    assert _BE_SENIOR_KNOWLEDGE in block


# ══════════════════════════════════════════════════════════════════════════════
# (2) Bài giảng: THÔI mang trần/sàn cấp độ, VẪN mang kiến thức
# ══════════════════════════════════════════════════════════════════════════════

def test_lesson_theory_khong_con_cau_tran_san_nhung_van_co_kien_thuc():
    prompt = build_lesson_theory_prompt(
        job_category="BE", level="Senior", lesson_title="Bài", focus_criteria=[], weaknesses=None)

    # Câu chặn viết cho prompt SINH CÂU HỎI — vô nghĩa với một bài giảng không sinh câu hỏi nào.
    assert "không được vượt lên cấp cao hơn" not in prompt
    # Cả khối mở đầu calibration cũng không còn — mất khối, không phải chỉ mất một câu.
    assert "CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN" not in prompt

    # Nội dung THẬT (kiến thức) vẫn phải còn — đây là phần task cấm làm mất.
    assert _BE_SENIOR_KNOWLEDGE in prompt


def test_lesson_theory_khong_co_job_category_thi_khong_co_gi_them():
    """Không seed nghề nào (mẫu `calibration_block(level)` không job_category) ⇒ không còn
    calibration lẫn không có knowledge — prompt không đổi gì thêm ở khe này."""
    prompt = build_lesson_theory_prompt(
        job_category="QA_UNSEEDED_CONTROL", level="Senior", lesson_title="Bài",
        focus_criteria=[], weaknesses=None)
    assert "không được vượt lên cấp cao hơn" not in prompt
    assert "CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN" not in prompt
    assert _BE_SENIOR_KNOWLEDGE not in prompt
