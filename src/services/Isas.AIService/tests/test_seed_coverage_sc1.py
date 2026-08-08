"""SC1 — prompt sinh câu hỏi phải ÉP PHÂN BỔ nhãn tiêu chí, không để model dồn cục.

Bằng chứng từ prod (buổi ``95ee0cc3``, BE/vi, 12 câu, đào sâu 3): 3 câu gốc nhưng nhãn ra
"Chiều sâu kỹ thuật" HAI lần ⇒ "Giải quyết vấn đề & thuật toán" không câu nào hỏi ⇒ bị loại khỏi
điểm (đúng thiết kế chấm-theo-phạm-vi) ⇒ điểm thành "may mắn trúng tủ".

Trước SC1 prompt chỉ nói "gắn nhãn cho ĐÚNG" (không bịa id, không gắn thừa, rỗng là hợp lệ) —
toàn bộ là ràng buộc trên TỪNG câu, không có ràng buộc nào trên CẢ BỘ. Model tuân thủ hoàn hảo mà
vẫn bỏ sót tiêu chí.
"""
import re

from app.prompts import build_prompt

C1 = "11111111-1111-1111-1111-111111111111"
C2 = "22222222-2222-2222-2222-222222222222"
C3 = "33333333-3333-3333-3333-333333333333"


def _criteria(n: int = 3) -> list[dict]:
    ids = [C1, C2, C3]
    names = ["Chiều sâu kỹ thuật", "Thiết kế hệ thống & CSDL", "Giải quyết vấn đề & thuật toán"]
    return [{"criterionId": ids[i], "name": names[i]} for i in range(n)]


# ══════════════════════════════════════════════════════════════════════════════
# (1) Đủ câu cho mọi tiêu chí ⇒ ép PHỦ HẾT
# ══════════════════════════════════════════════════════════════════════════════

def test_du_cau_thi_ep_moi_tieu_chi_co_it_nhat_mot_cau():
    prompt = build_prompt("BE", None, None, 5, None, None, _criteria(3))

    assert "PHÂN BỔ BẮT BUỘC" in prompt
    assert "MỖI tiêu chí trong 3 tiêu chí trên phải được ÍT NHẤT MỘT câu hỏi nhắm tới" in prompt
    # Nêu HẬU QUẢ, không chỉ ra lệnh — đây là phần model cần để tự cân khi phải chọn.
    assert "LOẠI khỏi kết quả chấm" in prompt


def test_ep_phu_khong_duoc_bien_thanh_giay_phep_gan_bua():
    """Ràng buộc PHỦ và luật CẤM-GẮN-THỪA kéo ngược chiều nhau. Nếu prompt chỉ nói "phủ cho đủ"
    thì đường rẻ nhất để model tuân thủ là dán thêm nhãn vào câu không hỏi về tiêu chí đó — tức
    tái tạo đúng lỗi mà chấm-theo-phạm-vi sinh ra để diệt, chỉ đổi chiều."""
    prompt = build_prompt("BE", None, None, 5, None, None, _criteria(3))

    assert "KHÔNG cho phép gắn bừa" in prompt
    assert "đổi NỘI DUNG câu hỏi" in prompt
    # …và các luật cũ vẫn còn nguyên, không bị nới để chiều ràng buộc mới.
    assert "KHÔNG gắn thêm cho 'đủ bộ'" in prompt
    assert "Rỗng là HỢP LỆ" in prompt
    assert "KHÔNG bịa id mới" in prompt


def test_ap_cho_ca_bo_khong_phai_tung_cau():
    """Câu xã giao vẫn được để rỗng — ràng buộc là trên tập câu hỏi, không phải trên mỗi câu.
    Thiếu vế này thì ép phủ sẽ mâu thuẫn trực diện với 'rỗng là hợp lệ'."""
    prompt = build_prompt("BE", None, None, 5, None, None, _criteria(3))
    assert "áp cho CẢ BỘ câu hỏi, không phải từng câu" in prompt


# ══════════════════════════════════════════════════════════════════════════════
# (2) Ít câu hơn tiêu chí ⇒ ép CHỌN KHÁC NHAU (phủ hết là bất khả thi)
# ══════════════════════════════════════════════════════════════════════════════

def test_it_cau_hon_tieu_chi_thi_ep_chon_tieu_chi_khac_nhau():
    prompt = build_prompt("BE", None, None, 2, None, None, _criteria(3))

    assert "Chỉ có 2 câu hỏi cho 3 tiêu chí" in prompt
    assert "không để hai câu cùng nhắm một tiêu chí" in prompt
    # KHÔNG được đòi phủ hết khi bất khả thi — đòi điều không làm được là mời model gắn bừa.
    assert "phải được ÍT NHẤT MỘT câu hỏi nhắm tới" not in prompt


def test_dung_bang_so_tieu_chi_van_la_nhanh_phu_het():
    """Biên count == n: vừa đủ để phủ hết, nên phải là nhánh 'phủ hết' chứ không phải nhánh 'chọn
    khác nhau' (hai nhánh nói khác nhau, off-by-one ở đây làm mất nửa số ca)."""
    prompt = build_prompt("BE", None, None, 3, None, None, _criteria(3))
    assert "MỖI tiêu chí trong 3 tiêu chí trên phải được ÍT NHẤT MỘT câu hỏi nhắm tới" in prompt
    assert "Chỉ có 3 câu hỏi cho 3 tiêu chí" not in prompt


# ══════════════════════════════════════════════════════════════════════════════
# (3) BẤT BIẾN — không thêm chữ nào khi không có gì để phân bổ
# ══════════════════════════════════════════════════════════════════════════════

def test_khong_criteria_thi_khong_co_khoi_phan_bo():
    """Campaign B2B và mọi caller cũ không gửi `criteria` ⇒ prompt phải giữ NGUYÊN XI."""
    p = build_prompt("BE", "CV", "JD", 5)
    assert "PHÂN BỔ BẮT BUỘC" not in p
    assert p == build_prompt("BE", "CV", "JD", 5, None, None, None)


def test_mot_tieu_chi_thi_khong_can_phan_bo():
    """n == 1: không có gì để trải đều — thêm khối chỉ tốn token mỗi lượt sinh."""
    p = build_prompt("BE", None, None, 5, None, None, _criteria(1))
    assert "PHÂN BỔ BẮT BUỘC" not in p
    # nhưng khối gắn nhãn thì vẫn phải có
    assert "targetCriterionIds" in p


# ══════════════════════════════════════════════════════════════════════════════
# (4) Thứ tự khối — ràng buộc phân bổ phải nằm SAU danh sách tiêu chí
# ══════════════════════════════════════════════════════════════════════════════

def test_khoi_phan_bo_nam_sau_danh_sach_tieu_chi():
    """Model đọc tuần tự: "MỖI tiêu chí trong 3 tiêu chí trên" chỉ có nghĩa nếu danh sách đã xuất
    hiện phía trên. Đảo thứ tự thì câu ràng buộc trỏ vào hư không."""
    prompt = build_prompt("BE", None, None, 5, None, None, _criteria(3))
    assert prompt.index("---HẾT TIÊU CHÍ NỘI DUNG---") < prompt.index("PHÂN BỔ BẮT BUỘC")


def test_so_tieu_chi_trong_prompt_khop_so_luong_thuc_te():
    """Con số trong câu ràng buộc phải là len(criteria) THẬT. Hardcode "3" sẽ đúng với seed hôm nay
    và sai im lặng với rubric riêng BC16 (candidate tự CRUD, số tiêu chí thay đổi được)."""
    prompt = build_prompt("BE", None, None, 9, None, None, _criteria(2))
    assert re.search(r"MỖI tiêu chí trong 2 tiêu chí trên", prompt)
    assert "3 tiêu chí trên" not in prompt
