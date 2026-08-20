"""``jdQuote`` — câu nguyên văn trong JD sinh ra requirement — phải VERIFY được, không phải tin model.

Trường này tồn tại để người dùng bấm "Xem trong JD" và tự kiểm chứng requirement lấy từ đâu. Một
quote bịa phá hỏng đúng thứ nó sinh ra để bảo đảm — và tệ hơn cả không có quote, vì người dùng sẽ
tin nó. Nên AIService đối chiếu quote với ``jdText`` rồi mới trả; không khớp ⇒ ``None``, giống kỷ
luật by-construction đang dùng cho ``chunkId`` (drop id lạ) và ``cvSections.startsWith``.

Lưu ý ``citations`` KHÔNG dùng được cho mục đích này: đó là tài liệu chuẩn ngành truy hồi từ Qdrant,
không phải trích từ JD của người dùng.
"""

from types import SimpleNamespace

import pytest

from app.providers.gemini import GeminiProvider, verify_jd_quote

JD_TEXT = (
    "Tuyển Backend Engineer.\n"
    "- Thành thạo C# và ASP.NET Core.\n"
    "- Có kinh nghiệm với Docker, Kubernetes.\n"
    "Ưu tiên ứng viên biết Terraform.\n"
)


# ── verify_jd_quote — đơn vị nhỏ nhất, test thẳng ────────────────────────────────────────
def test_quote_nguyen_van_duoc_giu():
    assert verify_jd_quote(JD_TEXT, "Thành thạo C# và ASP.NET Core") == "Thành thạo C# và ASP.NET Core"


def test_quote_bia_thanh_none():
    # Không có câu nào như vậy trong JD — nghe rất hợp lý, và đó chính là kiểu bịa nguy hiểm nhất.
    assert verify_jd_quote(JD_TEXT, "Yêu cầu 5 năm kinh nghiệm quản lý đội ngũ") is None


def test_dien_dat_lai_cung_bi_loai():
    # Đúng ý JD nhưng KHÔNG nguyên văn ⇒ loại: người dùng sẽ không tìm thấy đoạn này trong JD.
    assert verify_jd_quote(JD_TEXT, "Ứng viên cần giỏi C# và ASP.NET Core") is None


@pytest.mark.parametrize("quote", [None, "", "   ", 42, [], {"text": "C#"}])
def test_gia_tri_khong_dung_kieu_thanh_none(quote):
    assert verify_jd_quote(JD_TEXT, quote) is None


def test_khac_biet_khoang_trang_va_hoa_thuong_van_chap_nhan():
    # JD copy từ PDF/textarea hay bị gãy dòng, thụt lề; đây không phải bịa nên không loại.
    assert verify_jd_quote(JD_TEXT, "có   kinh\n nghiệm\tvới Docker") is not None


def test_ghep_hai_doan_roi_nhau_bi_loai():
    # Ghép "C#" ở dòng 1 với "Terraform" ở dòng cuối thành một "câu" không hề tồn tại.
    assert verify_jd_quote(JD_TEXT, "Thành thạo C# và biết Terraform") is None


# ── Đường đi thật trong suggest_jd_requirements ──────────────────────────────────────────
async def _run(monkeypatch, model_json: str) -> dict:
    provider = GeminiProvider()

    async def fake_generate(operation, *, contents, config, **kwargs):
        return SimpleNamespace(text=model_json)

    monkeypatch.setattr(provider, "_generate", fake_generate)
    return await provider.suggest_jd_requirements(JD_TEXT, "BE")


@pytest.mark.asyncio
async def test_suggest_giu_quote_that_va_bo_quote_bia(monkeypatch):
    result = await _run(monkeypatch, """
        {"mustHave": [
            {"text": "C#/ASP.NET Core", "citations": [],
             "jdQuote": "Thành thạo C# và ASP.NET Core"},
            {"text": "5 năm kinh nghiệm", "citations": [],
             "jdQuote": "Yêu cầu tối thiểu 5 năm kinh nghiệm"}],
         "niceToHave": [
            {"text": "Terraform", "citations": [],
             "jdQuote": "Ưu tiên ứng viên biết Terraform"}]}
    """)

    must = result["mustHave"]
    assert [x["text"] for x in must] == ["C#/ASP.NET Core", "5 năm kinh nghiệm"]
    assert must[0]["jdQuote"] == "Thành thạo C# và ASP.NET Core"
    # Quote bịa bị bỏ, nhưng requirement thì KHÔNG — text vẫn có giá trị với người dùng.
    assert must[1]["jdQuote"] is None
    assert result["niceToHave"][0]["jdQuote"] == "Ưu tiên ứng viên biết Terraform"


@pytest.mark.asyncio
async def test_suggest_khong_co_jdquote_van_chay(monkeypatch):
    """Model bản cũ/không trả field ⇒ None, không nổ."""
    result = await _run(monkeypatch, """
        {"mustHave": [{"text": "Docker", "citations": []}], "niceToHave": []}
    """)

    assert result["mustHave"][0]["jdQuote"] is None
