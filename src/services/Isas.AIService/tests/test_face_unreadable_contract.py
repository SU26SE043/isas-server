# tests/test_face_unreadable_contract.py — hợp đồng chéo AIService ↔ Campaign cho 422 ảnh hỏng.
import json
import pathlib

from app.face_verify import IMAGE_UNREADABLE_CODE


# ── HỢP ĐỒNG CHÉO với Campaign (.NET) ────────────────────────────────────────────
def _campaign_src(*parts: str) -> str:
    path = pathlib.Path(__file__).resolve().parents[2] / "Isas.CampaignService"
    for p in parts:
        path = path / p
    return path.read_text(encoding="utf-8")


def test_khoa_hop_dong_422_khop_client_campaign():
    """Mã + tên khoá + giá trị `image` mà Python trả phải là đúng những thứ client Campaign đọc.

    Lệch một ký tự là bug câm theo chiều NGUY HIỂM: Campaign không nhận ra "ảnh hỏng" ⇒ coi như
    hạ tầng ⇒ face-enroll fail-open nhận mốc rác lại y như trước bản vá. Mẫu JSON dưới đây cũng
    được dán NGUYÊN VĂN vào test C# (FaceUnreadableImageTests) — sửa một bên phải sửa cả hai."""
    sample = {"detail": {"code": IMAGE_UNREADABLE_CODE, "image": "reference",
                         "message": "Không giải mã được ảnh 'reference' (định dạng không hợp lệ hoặc dữ liệu hỏng)."}}
    assert json.dumps(sample, ensure_ascii=False)  # mẫu tự nó serialize được

    src = _campaign_src("Services", "AiServiceFaceVerifyClient.cs")
    assert f'"{IMAGE_UNREADABLE_CODE}"' in src
    for key in ('"detail"', '"code"', '"image"'):
        assert key in src, key
    # Tên ảnh mà controller rẽ nhánh (mốc → identity_unverified, live → no_face) khai ở hằng của exception.
    consts = _campaign_src("Services", "IAiServiceFaceVerifyClient.cs")
    assert 'Reference = "reference"' in consts
    assert 'Live = "live"' in consts


def test_mau_422_dan_nguyen_van_vao_test_csharp():
    """Body 422 mà AIService trả phải có mặt NGUYÊN VĂN trong test C# đi qua client thật — nếu ai sửa
    hình dạng/câu chữ ở một phía, test này buộc sửa phía kia (mẫu JSON escape kiểu C#)."""
    from app.face_verify import UnreadableImageError

    csharp = (pathlib.Path(__file__).resolve().parents[2] / "Isas.CampaignService.Tests"
              / "FaceUnreadableImageTests.cs").read_text(encoding="utf-8")
    for image in ("reference", "live", "image"):
        body = {"detail": {"code": IMAGE_UNREADABLE_CODE, "image": image,
                           "message": str(UnreadableImageError(image))}}
        literal = json.dumps(body, ensure_ascii=False, separators=(",", ":")).replace('"', '\\"')
        assert literal in csharp, image
