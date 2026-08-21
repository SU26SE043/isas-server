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

from app import prompt_registry

LEVELS: tuple[str, ...] = ("Fresher", "Junior", "Middle", "Senior")
DEFAULT = "Junior"

_logger = logging.getLogger(__name__)

# J4 — mỗi MỨC có khoá RIÊNG (`seniority.{level}.profile`), để admin sửa một mức không đụng ba
# mức còn lại. Mặc định của từng khoá là NGUYÊN VĂN dòng đang có trong code — registry rỗng ⇒
# `calibration_block` ra đúng byte như trước J4 (bất biến khoá bằng test, xem
# `test_seniority_wire_sen1.py::test_prompt_khong_truyen_thi_giu_nguyen_xi`).
_PROFILE_DEFAULTS: dict[str, str] = {
    "Fresher": (
        "Fresher: kiến thức nền tảng, khái niệm cốt lõi, tình huống đơn giản một bước. KHÔNG hỏi "
        "vận hành hệ thống quy mô lớn, KHÔNG hỏi đánh đổi kiến trúc."
    ),
    "Junior": (
        "Junior: áp dụng kiến thức vào công việc hằng ngày, gỡ lỗi thường gặp, quy trình làm việc "
        "nhóm; nghiêng về 'làm thế nào' hơn là 'vì sao chọn phương án này'."
    ),
    "Middle": (
        "Middle: thiết kế module, so sánh đánh đổi giữa các phương án, tối ưu hiệu năng, xử lý ca "
        "biên, rút kinh nghiệm từ dự án thật."
    ),
    "Senior": (
        "Senior: đánh đổi kiến trúc ở quy mô hệ thống, vận hành và độ tin cậy, chuẩn hoá kỹ thuật, "
        "ra quyết định dưới ràng buộc thực tế, dẫn dắt và định hướng người khác."
    ),
}


# BE-3 — kiến thức chuyên sâu THEO NGHỀ cho từng mức, khoá `category.{JOB}.seniority.{level}
# .knowledge` (đã tồn tại từ J4, registry luôn rỗng vì chưa ai seed). Cùng pattern
# `_PROFILE_DEFAULTS`: mặc định nằm trong SOURCE, registry vẫn ghi đè được từng cặp (nghề, mức)
# — admin sửa một mức của BA không đụng BE/FE hay 3 mức BA còn lại.
#
# Thang mặc định trước BE-3 nghiêng hẳn về kỹ thuật ("đánh đổi kiến trúc", "vận hành quy mô
# lớn") — hợp với BE/FE nhưng sai bản chất với BA, và BA chiếm 14/29 roadmap đo được (2026-08-21).
# Bốn dòng BA dưới đây nhắm THẲNG vào 3 tiêu chí NỘI DUNG của rubric BA (`B2CRubricSeed.cs`):
# "Phân tích yêu cầu" · "Hiểu nghiệp vụ & các bên liên quan" · "Tư duy giải quyết vấn đề" — không
# đụng "Giao tiếp & Tiếng Anh" (tiêu chí `Always`, không phải nội dung chuyên môn).
#
# ⚠ CHƯA HIỆU CHỈNH bằng JD/BA thật (team không có BA — xem §Rà soát roadmap 2026-08-21). Cố ý
# giữ mức độ vừa phải, tránh thuật ngữ có thể lệch thị trường VN (vd "ROI"/"business case" ở
# mức Fresher/Junior) — nếu sau này hiệu chỉnh sai thì sửa ĐÚNG DÒNG cần sửa qua registry, không
# cần deploy lại. Seed BE/FE để trống có chủ đích: `test_prompt_khong_truyen_thi_giu_nguyen_xi`
# chạy trên nghề BE và phải giữ nguyên byte — seed thêm BE/FE PHẢI re-check test đó trước.
_KNOWLEDGE_DEFAULTS: dict[str, dict[str, str]] = {
    "BA": {
        "Fresher": (
            "Kiến thức BA mức Fresher: khái niệm nền — user story, use case, đặc tả yêu cầu "
            "(SRS), phân biệt yêu cầu chức năng/phi chức năng. Câu hỏi nên xoay quanh đọc hiểu "
            "MỘT yêu cầu cụ thể và đặt câu hỏi làm rõ với một stakeholder — KHÔNG hỏi đàm phán "
            "đa bên liên quan hay phân tích rủi ro cả dự án."
        ),
        "Junior": (
            "Kiến thức BA mức Junior: tự viết user story/use case hoàn chỉnh cho một tính năng, "
            "chạy workshop thu thập yêu cầu với 1-2 stakeholder, viết acceptance criteria rõ "
            "ràng, phát hiện yêu cầu mơ hồ/thiếu và hỏi lại đúng chỗ. Câu hỏi nên xoay quanh tình "
            "huống thực tế: khách hàng đổi ý giữa chừng, yêu cầu chồng chéo giữa hai bộ phận."
        ),
        "Middle": (
            "Kiến thức BA mức Middle: chủ trì workshop nhiều stakeholder có quan điểm mâu thuẫn, "
            "vẽ quy trình nghiệp vụ (process mapping), phân tích đánh đổi giữa các phương án "
            "giải pháp (phạm vi vs thời hạn, xây vs mua), đánh giá tác động khi yêu cầu đổi giữa "
            "dự án. Câu hỏi nên đào sâu cách xử lý xung đột lợi ích và ưu tiên hoá backlog theo "
            "giá trị nghiệp vụ."
        ),
        "Senior": (
            "Kiến thức BA mức Senior: định hình giải pháp cho cả một mảng nghiệp vụ, cân bằng "
            "ràng buộc kỹ thuật-ngân sách-chính trị nội bộ, dẫn dắt BA/PO junior, chịu trách "
            "nhiệm chất lượng yêu cầu ở quy mô nhiều dự án. Câu hỏi nên xoay quanh cách ra quyết "
            "định khi thiếu thông tin, thuyết phục stakeholder cấp cao, và đo lường giá trị "
            "nghiệp vụ sau triển khai."
        ),
    },
}


def _profile_key(level: str) -> str:
    return f"seniority.{level}.profile"


def _scoring_focus_key(level: str) -> str:
    return f"seniority.{level}.scoring_focus"


def _knowledge_key(job_category: str, level: str) -> str:
    return f"category.{job_category.upper()}.seniority.{level}.knowledge"


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


def calibration_block(level: str, job_category: str | None = None) -> str:
    """Khối hiệu chỉnh độ khó câu gốc theo cấp độ (J4: đọc registry, KHÔNG hard-code).

    Giữ tiếng Việt kể cả với buổi tiếng Anh — đồng nhất với `build_decide_next_prompt` và với toàn
    bộ phần chỉ thị của `build_prompt`; ngôn ngữ ĐẦU RA đã được ép riêng bằng `output_directive`.

    Bảng 4 mức vẫn được liệt kê ĐỦ (mỗi dòng đọc từ khoá RIÊNG của chính mức đó qua
    `prompt_registry`, mặc định = nguyên văn dòng cũ) — admin sửa một mức không đụng ba mức còn
    lại, và registry rỗng cho ra đúng cùng một khối như trước J4.

    Câu cuối cố ý nói rõ quan hệ với dòng *"đi từ cơ bản đến nâng cao"* ở đầu prompt: không có nó,
    hai chỉ thị đọc như mâu thuẫn và mô hình tự do chọn bên nào cũng được ⇒ hiệu chỉnh mất tác dụng
    đúng ở cấp Fresher/Senior (hai đầu thang, nơi nó quan trọng nhất).

    ``job_category`` (J4, seed BE-3): có thì thêm khối kiến thức chuyên sâu riêng cho CẶP (nghề,
    mức) đang dùng — đọc `category.{JOB}.seniority.{level}.knowledge` qua registry, mặc định rơi
    về :data:`_KNOWLEDGE_DEFAULTS`. Cặp KHÔNG có trong cả registry lẫn default (mọi nghề ngoài BA
    tính tới BE-3) ⇒ khối này không xuất hiện, prompt không đổi một byte cho nghề đó — đúng bất
    biến J4 cũ, chỉ thu hẹp phạm vi còn "nghề chưa được seed" thay vì "mọi nghề".
    """
    lines = "\n".join(
        f"- {prompt_registry.get(_profile_key(lv), _PROFILE_DEFAULTS[lv])}" for lv in LEVELS
    )
    block = (
        f"CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: {level}\n"
        "Toàn bộ câu hỏi PHẢI được hiệu chỉnh ĐÚNG TẦM cấp độ này — đây là lựa chọn tường minh của "
        "người dùng, không phải gợi ý:\n"
        f"{lines}\n"
        f"CHỈ áp dụng dòng ứng với cấp độ {level}; bỏ qua các dòng còn lại. Thang 'đi từ cơ bản đến "
        f"nâng cao' nêu trên áp dụng TRONG phạm vi cấp độ {level}, không được vượt lên cấp cao hơn "
        "hay tụt xuống cấp thấp hơn."
    )

    default_knowledge = (
        _KNOWLEDGE_DEFAULTS.get(job_category.upper(), {}).get(level, "") if job_category else ""
    )
    knowledge = (
        prompt_registry.get(_knowledge_key(job_category, level), default_knowledge)
        if job_category else ""
    )
    if knowledge:
        block += f"\n{knowledge}"
    return block


def scoring_focus(level: str) -> str:
    """J5 — trọng tâm CHẤM riêng theo cấp độ, chèn vào ĐUÔI prompt CHẤM (`extra_bits`, SAU mọi
    luật bắt buộc — bất biến F21, KHÔNG mở khe mới ở vị trí mới).

    Mặc định RỖNG: khe rỗng ⇒ `build_scoring_prompt` không đổi một byte, kể cả khi
    ``seniority`` được truyền — đây là bất biến âm quan trọng nhất của J5, cho phép đo tách bạch
    ảnh hưởng của J1 (luật công bằng) với J5 (cấp độ) trên dữ liệu thật.
    """
    return prompt_registry.get(_scoring_focus_key(level), "")
