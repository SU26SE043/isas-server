# tests/test_weakness_repeat_count_rec1b1.py — REC1-B1: WeaknessScore mang thêm `weakSessions` +
# `totalSessions` (bao nhiêu BUỔI trong SỐ buổi đã chọn từng gắn cờ NeedsImprovement cho tiêu chí
# này, trên tổng số buổi). "Yếu 3/4 buổi" đáng tin hơn hẳn "yếu 1/4 buổi" dù `percentage` (điểm ở
# MỘT buổi) giống nhau — thiếu mẫu số này thì model không biết tin phần trăm đó tới đâu.
#
# 🔴 Đây là lần thứ 5 trong repo của cùng một bẫy: field .NET gửi lên nhưng pydantic
# `extra='ignore'` NUỐT IM LẶNG vì KHÔNG được khai tường minh — HTTP 200, không lỗi, không log,
# tính năng chết câm (`focusCriteria`/BC14 · `metricsVersion` · `seniority`/SEN1 ·
# `lessonContext` · nay `weakSessions`/`totalSessions`).
from app.prompts import build_roadmap_prompt
from app.schemas import WeaknessScore


# ══════════════════ (1) HỢP ĐỒNG DÂY — pydantic không được nuốt ══════════════════

def test_weakness_score_field_set():
    """Khoá CẢ TẬP TÊN — đọc từ CLASS (mẫu test_roadmap_mistakes_wire.py). Xoá dòng khai
    `weakSessions`/`totalSessions` khỏi WeaknessScore ⇒ test này ĐỎ ngay, không cần chạy hết
    pipeline mới phát hiện field bị nuốt câm."""
    assert set(WeaknessScore.model_fields) == {
        "criterionName", "percentage", "weakSessions", "totalSessions",
    }


def test_weakness_score_nhan_weak_sessions_tu_json_tho():
    """Dựng từ JSON THÔ đúng như .NET gửi (AiServiceRoadmapGenerator.cs) — không phải dựng
    trực tiếp bằng constructor Python, để bắt được đúng lớp bug 'field gửi lên nhưng rớt ở
    model_validate'."""
    w = WeaknessScore.model_validate({
        "criterionName": "Thiết kế CSDL", "percentage": 40,
        "weakSessions": 3, "totalSessions": 4,
    })
    assert w.weakSessions == 3
    assert w.totalSessions == 4


def test_weakness_score_khong_gui_weak_sessions_thi_mac_dinh_0():
    """Đối chứng — caller CŨ (chưa gửi 2 trường mới, ví dụ test cũ dựng payload tay) KHÔNG được
    vỡ; mặc định 0 giữ hành vi cũ khi build_roadmap_prompt không in phần '(tái phạm x/y buổi)'."""
    w = WeaknessScore.model_validate({"criterionName": "SQL", "percentage": 40})
    assert w.weakSessions == 0
    assert w.totalSessions == 0


# ══════════════════ (2) PROMPT — chỉ in "(tái phạm x/y buổi)" khi có dữ liệu thật ══════════════════

def test_prompt_in_ti_le_tai_pham_khi_co_weak_sessions():
    prompt = build_roadmap_prompt(
        "BE", "Junior",
        weaknesses=[{"criterionName": "SQL", "percentage": 40, "weakSessions": 3, "totalSessions": 4}],
    )
    assert "SQL: 40%" in prompt
    assert "(tái phạm 3/4 buổi)" in prompt


def test_prompt_khong_in_ti_le_tai_pham_khi_thieu_weak_sessions():
    """Payload CŨ (không mang weakSessions/totalSessions, hoặc weakSessions=0) → giữ NGUYÊN VĂN
    dòng cũ, không in '(tái phạm 0/0 buổi)' vô nghĩa."""
    prompt = build_roadmap_prompt(
        "BE", "Junior",
        weaknesses=[{"criterionName": "SQL", "percentage": 40}],
    )
    assert "- SQL: 40%" in prompt
    assert "tái phạm" not in prompt
