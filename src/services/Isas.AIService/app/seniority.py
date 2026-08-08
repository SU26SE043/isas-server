"""Chính sách CẤP ĐỘ ỨNG VIÊN (seniority) cho lớp SINH câu hỏi (SEN1).

`seniority` là lựa chọn của NGƯỜI DÙNG (B2C ứng viên tự khai / B2B do HR đặt cấp chiến dịch),
được đóng dấu xuống `practice_sessions.seniority` · `campaigns.seniority` và đã đi vào
`/decide-next` từ trước. Nhưng nó CHƯA BAO GIỜ tới `/generate-questions` — nghĩa là bộ **câu gốc**
(thứ định khung cả buổi: mặc định 5/20 câu, mỗi câu gốc còn được đào sâu tối đa 3 tầng quanh chính
chủ đề nó mở ra — INT-17b) hoàn toàn không đếm xỉa tới mức đã chọn. Ứng viên chọn *Senior* nhận bộ
câu gốc y hệt *Fresher*; chỉ câu đào sâu mới calibrate.

⚠ AIService KHÔNG tự bảo vệ được giá trị này. .NET có validate tập đóng
(`PracticeService.ValidateSeniority`, so **case-sensitive**, khớp CHECK `ck_practice_sessions_seniority`
và `ck_campaigns_seniority` ở DB) nhưng đây là endpoint HTTP, caller khác gửi gì cũng được ⇒ chuẩn hoá
tại chỗ.

**FAIL-OPEN CÓ CHỦ ĐÍCH — giá trị lạ ⇒ `Junior` + log, TUYỆT ĐỐI KHÔNG raise.** `main.py` bọc mọi
exception của `/generate-questions` thành **502**, mà đường sinh câu hỏi nằm SAU `ReserveAsync`
(PAY-5): raise ở đây biến một chuỗi gõ sai thành buổi luyện hỏng ĐÃ TRỪ CREDIT. Cùng lý do với
`fullName` (BK28) và `targetCriterionIds` (chấm-theo-phạm-vi) — chi tiết phụ không được phép làm
hỏng cả buổi.
"""

import logging

LEVELS: tuple[str, ...] = ("Fresher", "Junior", "Middle", "Senior")
DEFAULT = "Junior"

_logger = logging.getLogger(__name__)


def normalize(value: str | None) -> str:
    """Đưa `seniority` về đúng một trong :data:`LEVELS`; mọi thứ khác → :data:`DEFAULT`.

    So khớp **case-sensitive** sau khi `strip()` — cố ý giữ nguyên thang hoa/thường của .NET và DB
    thay vì nhận `"senior"`: chấp nhận biến thể hoa/thường ở đây là mở một cửa hậu để chuỗi không
    lưu được xuống DB vẫn chạy trơn trên đường AI, rồi hai bên lệch nhau mà không ai báo.
    """
    level = (value or "").strip()
    if level in LEVELS:
        return level
    _logger.warning(
        "seniority '%s' không thuộc %s — dùng '%s'", value, "/".join(LEVELS), DEFAULT)
    return DEFAULT


def calibration_block(level: str) -> str:
    """Khối hiệu chỉnh độ khó câu gốc theo cấp độ.

    Giữ tiếng Việt kể cả với buổi tiếng Anh — đồng nhất với `build_decide_next_prompt` và với toàn
    bộ phần chỉ thị của `build_prompt`; ngôn ngữ ĐẦU RA đã được ép riêng bằng `output_directive`.

    Câu cuối cố ý nói rõ quan hệ với dòng *"đi từ cơ bản đến nâng cao"* ở đầu prompt: không có nó,
    hai chỉ thị đọc như mâu thuẫn và mô hình tự do chọn bên nào cũng được ⇒ hiệu chỉnh mất tác dụng
    đúng ở cấp Fresher/Senior (hai đầu thang, nơi nó quan trọng nhất).
    """
    return (
        f"CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: {level}\n"
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
        f"CHỈ áp dụng dòng ứng với cấp độ {level}; bỏ qua các dòng còn lại. Thang 'đi từ cơ bản đến "
        f"nâng cao' nêu trên áp dụng TRONG phạm vi cấp độ {level}, không được vượt lên cấp cao hơn "
        "hay tụt xuống cấp thấp hơn."
    )
