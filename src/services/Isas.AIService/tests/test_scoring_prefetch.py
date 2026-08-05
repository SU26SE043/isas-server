# tests/test_scoring_prefetch.py — trần ĐỒNG THỜI của worker chấm.
#
# Vì sao cần test riêng cho một con số config: giá trị cũ ghi CỨNG `1` ngay trong `main()`, mà
# `main()` mở kết nối RabbitMQ thật nên không unit-test được ⇒ ai sửa nhầm về 1 (hoặc bỏ quên
# `set_qos`) sẽ KHÔNG có test nào đỏ, trong khi hậu quả là throughput chấm tụt 4 lần MÀ KHÔNG
# CÓ LỖI NÀO — đúng loại hỏng âm thầm. Ở đây tách phần kiểm được (giá trị config + việc worker
# đọc config thay vì hằng số) ra khỏi phần cần broker.
#
# Không cần broker: dùng AsyncMock channel (mẫu `declare_topology` của AI2).
import inspect

from app import worker
from app.config import Settings, settings


def test_prefetch_mac_dinh_cho_song_song():
    """Mặc định PHẢI > 1 — đây là toàn bộ lý do tồn tại của thay đổi này.

    Đo thật 2026-08-04: 1 lượt chấm 12,6s, 4 lượt song song 13,3s (chờ mạng Gemini chứ không
    CPU) ⇒ giữ 1 là vứt không throughput. Khoá cả cận trên: quá cao thì một đợt job đường TĨNH
    sẽ chạy ngần đó Whisper cùng lúc và bóp nghẹt CPU máy chạy worker.
    """
    assert settings.scoring_prefetch > 1, (
        "prefetch=1 làm worker chấm tuần tự — mất ~4x throughput mà không có lỗi nào báo")
    assert settings.scoring_prefetch <= 16, (
        "prefetch quá cao: mỗi message có thể kéo theo 1 lượt Whisper (đường tĩnh) + 1 lượt Gemini")


def test_prefetch_doc_tu_env():
    """Chỉnh được lúc chạy — hạ nhanh khi Gemini 429 mà không phải build lại image."""
    assert Settings(scoring_prefetch=3).scoring_prefetch == 3


def test_worker_dung_config_chu_khong_phai_hang_so():
    """Chốt rằng `main()` đọc `settings.scoring_prefetch`.

    Đọc thẳng mã nguồn vì `main()` mở kết nối thật nên không gọi được trong test. Mutation:
    ghi cứng lại `set_qos(prefetch_count=1)` → test này ĐỎ.
    """
    src = inspect.getsource(worker.main)
    assert "set_qos" in src, "worker.main() không còn đặt QoS — prefetch về mặc định của broker"
    assert "settings.scoring_prefetch" in src, (
        "worker.main() phải lấy prefetch từ config, không ghi cứng")


def test_scoring_va_cv_screening_tach_tran_rieng():
    """Hai luồng phải giữ trần RIÊNG.

    Gộp một con số là mất đúng lý do C14 tách channel: backlog audio (nặng, có Whisper) không
    được nghẽn sàng CV (nhẹ) và ngược lại.
    """
    assert hasattr(settings, "cv_screening_prefetch")
    assert (Settings(scoring_prefetch=7).cv_screening_prefetch
            == settings.cv_screening_prefetch), "đổi trần chấm không được kéo theo trần sàng CV"
