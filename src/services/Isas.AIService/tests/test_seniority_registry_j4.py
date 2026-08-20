# tests/test_seniority_registry_j4.py — J4: calibration_block đọc registry thay vì hard-code.
#
# Bối cảnh: `calibration_block` (dùng cho prompt SINH câu hỏi qua `build_prompt` và cho prompt
# sinh MỐC ĐIỂM qua `build_criterion_levels_prompt`) trước đây hard-code cả 4 dòng mô tả cấp độ
# NGAY TRONG CODE — admin không sửa được, phải deploy lại. J4 đưa từng dòng vào registry, MỖI
# MỨC một khoá riêng (`seniority.{level}.profile`), để sửa một mức không đụng ba mức còn lại.
# Đồng thời thêm khối "kiến thức chuyên sâu" theo cặp (nghề, mức) — mặc định rỗng.
#
# Ba bất biến phải giữ, đúng thứ tự mức độ quan trọng:
#   (1) Registry HOÀN TOÀN rỗng ⇒ prompt giống hệt TỪNG BYTE bản hard-code trước J4. Đây là điều
#       kiện để phép đo `benchmark-scoring.py` (do người có quyền server chạy sau) tách bạch được
#       ảnh hưởng của J4 với các thay đổi khác — thiếu bất biến này thì đo cái gì cũng vô nghĩa.
#   (2) Sửa MỘT khoá profile chỉ đổi dòng của MỨC đó — 3 mức còn lại + khung câu dẫn/câu chốt
#       nguyên vẹn.
#   (3) Khoá kiến thức theo (nghề, mức): mặc định rỗng (không xuất hiện gì thêm); có nội dung thì
#       xuất hiện; hai nghề khác nhau cho ra hai khối khác nhau (không rò rỉ chéo nghề).

import pytest

from app import prompt_registry
from app import seniority as seniority_module
from app.prompts import build_prompt


@pytest.fixture(autouse=True)
def _clean_registry():
    """Registry là cache module-level — không dọn thì test này rò sang test kia (mẫu test_prompt_
    registry_f21.py)."""
    prompt_registry.reset_cache()
    yield
    prompt_registry.reset_cache()


def _prompt(**kwargs) -> str:
    return build_prompt("BE", None, None, 5, **kwargs)


# Sao y NGUYÊN VĂN cấu trúc chuỗi của hàm `calibration_block` TRƯỚC J4 (cùng cách nối chuỗi,
# cùng thứ tự) — dùng làm mốc so sánh byte-identical. `{level}` xuất hiện 3 lần trong bản gốc.
_OLD_CALIBRATION_BLOCK_TEMPLATE = (
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


# ══════════════════════════════════════════════════════════════════════════════
# (1) Registry rỗng ⇒ byte-identical với bản hard-code cũ
# ══════════════════════════════════════════════════════════════════════════════

@pytest.mark.parametrize("level", seniority_module.LEVELS)
def test_registry_rong_calibration_block_giong_het_ban_hardcode_cu(level):
    expected = _OLD_CALIBRATION_BLOCK_TEMPLATE.format(level=level)
    assert seniority_module.calibration_block(level) == expected


def test_registry_rong_calibration_block_voi_job_category_cung_giong_het():
    """Truyền `job_category` (mới ở J4) mà registry rỗng ⇒ vẫn không đổi một byte — khối kiến
    thức chuyên sâu mặc định rỗng nên không được nối thêm gì."""
    expected = _OLD_CALIBRATION_BLOCK_TEMPLATE.format(level="Senior")
    assert seniority_module.calibration_block("Senior", "BE") == expected
    assert seniority_module.calibration_block("Senior", job_category=None) == expected


def test_registry_rong_prompt_sinh_giong_het_truoc_j4():
    expected_block = _OLD_CALIBRATION_BLOCK_TEMPLATE.format(level="Senior")
    assert expected_block in _prompt(seniority="Senior")


# ══════════════════════════════════════════════════════════════════════════════
# (2) Sửa MỘT mức chỉ đổi dòng của mức đó — ba mức còn lại + khung nguyên vẹn
# ══════════════════════════════════════════════════════════════════════════════

def test_sua_mot_khoa_profile_chi_doi_dong_cua_muc_do():
    prompt_registry._cache = {"seniority.Middle.profile": "Middle: ĐÃ TUỲ BIẾN bởi admin."}

    block = seniority_module.calibration_block("Senior")

    assert "- Middle: ĐÃ TUỲ BIẾN bởi admin." in block
    assert "- Middle: thiết kế module" not in block
    # Ba mức còn lại + câu dẫn + câu chốt PHẢI nguyên vẹn.
    assert "- Fresher: kiến thức nền tảng, khái niệm cốt lõi" in block
    assert "- Junior: áp dụng kiến thức vào công việc hằng ngày" in block
    assert "- Senior: đánh đổi kiến trúc ở quy mô hệ thống" in block
    assert "CHỈ áp dụng dòng ứng với cấp độ Senior; bỏ qua các dòng còn lại." in block


def test_sua_khoa_profile_cua_muc_khong_lien_quan_khong_anh_huong_muc_dang_chon():
    """Sửa dòng Fresher trong khi buổi đang chọn Senior — dòng Senior không đổi, vẫn hard-code."""
    prompt_registry._cache = {"seniority.Fresher.profile": "Fresher: BẢN SỬA."}
    block = seniority_module.calibration_block("Senior")
    assert "- Senior: đánh đổi kiến trúc ở quy mô hệ thống" in block


def test_manh_rong_khong_ghi_de_ban_mac_dinh_cua_profile():
    """Một mảnh toàn khoảng trắng lọt vào cache PHẢI vô hại — mẫu `prompt_registry.get()`."""
    prompt_registry._cache = {"seniority.Junior.profile": "   "}
    block = seniority_module.calibration_block("Junior")
    assert "- Junior: áp dụng kiến thức vào công việc hằng ngày" in block


# ══════════════════════════════════════════════════════════════════════════════
# (3) Khối kiến thức chuyên sâu theo (nghề, mức) — mặc định rỗng, không rò chéo nghề
# ══════════════════════════════════════════════════════════════════════════════

def test_khong_khai_job_category_thi_khong_co_khoi_kien_thuc():
    block = seniority_module.calibration_block("Senior")
    # Không tra registry chút nào khi job_category vắng — khối kiến thức không thể xuất hiện.
    assert block == _OLD_CALIBRATION_BLOCK_TEMPLATE.format(level="Senior")


def test_khoa_kien_thuc_rong_khong_them_gi():
    block = seniority_module.calibration_block("Senior", "BE")
    assert block == _OLD_CALIBRATION_BLOCK_TEMPLATE.format(level="Senior")


def test_khoa_kien_thuc_co_noi_dung_thi_xuat_hien():
    prompt_registry._cache = {
        "category.BE.seniority.Senior.knowledge": "Senior BE nên biết về sharding và CAP."
    }
    block = seniority_module.calibration_block("Senior", "BE")
    assert "Senior BE nên biết về sharding và CAP." in block
    # Nối SAU khung bắt buộc (không thay thế khung).
    assert block.index("CHỈ áp dụng dòng ứng với cấp độ Senior") < block.index("sharding và CAP")


def test_nghe_khac_nhau_ra_khoi_kien_thuc_khac_nhau():
    prompt_registry._cache = {
        "category.BE.seniority.Senior.knowledge": "BE_KNOWLEDGE_MARKER",
        "category.FE.seniority.Senior.knowledge": "FE_KNOWLEDGE_MARKER",
    }
    be_block = seniority_module.calibration_block("Senior", "BE")
    fe_block = seniority_module.calibration_block("Senior", "FE")

    assert "BE_KNOWLEDGE_MARKER" in be_block
    assert "FE_KNOWLEDGE_MARKER" not in be_block
    assert "FE_KNOWLEDGE_MARKER" in fe_block
    assert "BE_KNOWLEDGE_MARKER" not in fe_block


def test_khoa_kien_thuc_khong_ro_ri_sang_muc_khac_cung_nghe():
    """Kiến thức khai cho (BE, Senior) không được lộ ra khi chấm/sinh cho (BE, Fresher)."""
    prompt_registry._cache = {
        "category.BE.seniority.Senior.knowledge": "CHI_DANH_CHO_SENIOR",
    }
    fresher_block = seniority_module.calibration_block("Fresher", "BE")
    assert "CHI_DANH_CHO_SENIOR" not in fresher_block


def test_job_category_thuong_hoa_deu_tra_ve_cung_khoa():
    """`calibration_block` nhận `job_category` như caller gửi (vd 'BE' từ enum .NET) — khoá dựng
    bằng `.upper()` (mẫu `_category_key` của category_guidance/category_display_name)."""
    prompt_registry._cache = {"category.BE.seniority.Senior.knowledge": "MARKER"}
    assert "MARKER" in seniority_module.calibration_block("Senior", "be")
    assert "MARKER" in seniority_module.calibration_block("Senior", "BE")


# ══════════════════════════════════════════════════════════════════════════════
# (4) Khe thứ hai — scoring_focus (J5) mặc định rỗng, độc lập với profile/knowledge
# ══════════════════════════════════════════════════════════════════════════════

def test_scoring_focus_mac_dinh_rong():
    assert seniority_module.scoring_focus("Senior") == ""


def test_scoring_focus_co_noi_dung_thi_tra_dung_noi_dung():
    prompt_registry._cache = {"seniority.Senior.scoring_focus": "Chấm nặng về đánh đổi kiến trúc."}
    assert seniority_module.scoring_focus("Senior") == "Chấm nặng về đánh đổi kiến trúc."


def test_scoring_focus_khong_dung_chung_khoa_voi_profile():
    """`seniority.Senior.profile` và `seniority.Senior.scoring_focus` PHẢI là hai khoá riêng —
    sửa cái này không được vô tình đổi cái kia."""
    prompt_registry._cache = {"seniority.Senior.profile": "Senior: BẢN SỬA PROFILE."}
    assert seniority_module.scoring_focus("Senior") == ""
