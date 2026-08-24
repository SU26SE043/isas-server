# tests/test_seniority_wire_sen1.py — SEN1: cấp độ ứng viên phải tới được LỚP SINH câu hỏi.
#
# VẤN ĐỀ ĐANG SỐNG (đo 2026-08-08): `seniority` đã có ở `practice_sessions.seniority` /
# `campaigns.seniority`, có CHECK constraint, được validate trước `ReserveAsync` và ĐÃ đi vào
# `/decide-next` — nhưng KHÔNG BAO GIỜ tới `/generate-questions`. Ứng viên chọn *Senior* nhận bộ
# CÂU GỐC y hệt *Fresher*, mà câu gốc mới là thứ định khung cả buổi (mặc định 5/20 câu, và INT-17b
# cho mỗi câu gốc đào sâu tối đa 3 tầng quanh chính chủ đề nó mở ra) ⇒ lựa chọn người dùng vừa trả
# tiền bị bỏ qua ở đúng phần quan trọng nhất.
#
# Bốn thứ được khoá ở đây:
#   (1) HỢP ĐỒNG DÂY — literal `seniority` PHẢI được khai tường minh trong `GenerateQuestionsRequest`.
#       Đây là lớp DUY NHẤT chặn được kiểu hỏng đã cắn repo BA lần (`focusCriteria` bị pydantic nuốt ·
#       `metricsVersion` rụng khỏi schema response · `adaptiveMaxQuestions` vs `maxQuestions`): .NET
#       gửi, HTTP 200, không lỗi, không log — prompt chỉ đơn giản không đổi một chữ.
#   (2) ĐI HẾT DÂY — endpoint phải TRUYỀN XUỐNG provider (khai schema mà quên truyền = hỏng y hệt).
#   (3) PROMPT THẬT SỰ ĐỔI theo cấp độ (nhận tham số mà không dùng = hỏng y hệt).
#   (4) BẤT BIẾN "không truyền ⇒ không đổi một byte nào" — mọi caller nội bộ chưa wire giữ nguyên.
#
# Không gọi Gemini thật.
import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app import seniority as seniority_module
from app.config import settings
from app.prompts import build_prompt
from app.providers.gemini import QuestionGenerationResult
from app.schemas import GenerateQuestionsRequest

client = TestClient(main_module.app)

# Q2/GEN-7 — endpoint SINH gate X-Internal-Token (fail-closed).
_HEADERS = {"X-Internal-Token": settings.internal_token}

LEVELS = ("Fresher", "Junior", "Middle", "Senior")


# ══════════════════════════════════════════════════════════════════════════════
# 1. HỢP ĐỒNG DÂY — literal tên khoá + mặc định
# ══════════════════════════════════════════════════════════════════════════════

def test_request_khai_tuong_minh_seniority():
    """Mẫu `test_bilingual_wire`: khai thiếu ⇒ pydantic `extra='ignore'` nuốt im lặng."""
    assert "seniority" in GenerateQuestionsRequest.model_fields


def test_seniority_mac_dinh_la_junior():
    """Khớp DEFAULT của `practice_sessions.seniority` / `campaigns.seniority` ở DB."""
    assert GenerateQuestionsRequest(jobCategory="BE").seniority == "Junior"


def test_seniority_doc_dung_khoa_camelcase_tu_json():
    """Đúng NGUYÊN VĂN khoá .NET gửi. Lệch hoa/thường ⇒ rơi về mặc định trong im lặng."""
    req = GenerateQuestionsRequest.model_validate({"jobCategory": "BE", "seniority": "Senior"})
    assert req.seniority == "Senior"

    # Đối chứng ÂM: PascalCase (kiểu .NET serialize mặc định khi quên naming policy) KHÔNG được
    # nhận — nếu nó cũng ra "Senior" thì phép trên không chứng minh được gì về tên khoá.
    assert GenerateQuestionsRequest.model_validate(
        {"jobCategory": "BE", "Seniority": "Senior"}).seniority == "Junior"


# ══════════════════════════════════════════════════════════════════════════════
# 2. ĐI HẾT DÂY — endpoint → provider
# ══════════════════════════════════════════════════════════════════════════════

def _capture_generate(bucket):
    async def fake_generate(job_category, cv_text, jd_text, count=None,
                            focus_criteria=None, grounding=None, criteria=None,
                            seniority=None, lesson_context=None, topics=None):
        bucket.append(seniority)
        return QuestionGenerationResult(questions=["Q1"], citations=None)
    return fake_generate


@pytest.mark.parametrize("level", LEVELS)
def test_endpoint_truyen_seniority_xuong_provider(monkeypatch, level):
    seen: list[str | None] = []
    monkeypatch.setattr(main_module.provider, "generate", _capture_generate(seen))

    res = client.post("/api/v1/generate-questions", headers=_HEADERS,
                      json={"jobCategory": "BE", "seniority": level})

    assert res.status_code == 200, res.text
    assert seen == [level]


def test_endpoint_khong_gui_seniority_van_truyen_junior(monkeypatch):
    """Caller cũ (Campaign B2B trước khi wire) → nhận đúng mặc định, KHÔNG phải None."""
    seen: list[str | None] = []
    monkeypatch.setattr(main_module.provider, "generate", _capture_generate(seen))

    res = client.post("/api/v1/generate-questions", headers=_HEADERS, json={"jobCategory": "BE"})

    assert res.status_code == 200, res.text
    assert seen == ["Junior"]


def test_endpoint_seniority_la_va_van_200(monkeypatch):
    """Fail-open: chuỗi lạ KHÔNG được thành 422/502 — đường này đã TRỪ CREDIT (PAY-5)."""
    seen: list[str | None] = []
    monkeypatch.setattr(main_module.provider, "generate", _capture_generate(seen))

    res = client.post("/api/v1/generate-questions", headers=_HEADERS,
                      json={"jobCategory": "BE", "seniority": "CEO"})

    assert res.status_code == 200, res.text
    # Chuẩn hoá nằm ở lớp prompt (phủ MỌI đường vào), không phải ở schema.
    assert seen == ["CEO"]


# ══════════════════════════════════════════════════════════════════════════════
# 3. CHUẨN HOÁ — giá trị lạ → Junior, không bao giờ raise
# ══════════════════════════════════════════════════════════════════════════════

@pytest.mark.parametrize("level", LEVELS)
def test_normalize_giu_nguyen_muc_hop_le(level):
    assert seniority_module.normalize(level) == level


@pytest.mark.parametrize("bad", ["senior", "SENIOR", "", "   ", "CEO", "Intern", None, "Junior "])
def test_normalize_gia_tri_la_ve_junior_khong_nem(bad):
    """`"Junior "` có khoảng trắng thừa → strip rồi khớp, ra "Junior" (không phải fallback)."""
    assert seniority_module.normalize(bad) == "Junior"


def test_normalize_case_sensitive_khop_check_constraint_db():
    """Case-sensitive khớp `ck_practice_sessions_seniority` + `PracticeService.ValidateSeniority`.

    Nhận "senior" ở đây là mở cửa hậu cho một chuỗi KHÔNG lưu được xuống DB vẫn chạy trơn trên
    đường AI, rồi hai bên lệch nhau mà không ai báo.
    """
    assert seniority_module.normalize("senior") != "Senior"


def test_normalize_ghi_log_khi_gia_tri_la(caplog):
    with caplog.at_level("WARNING"):
        seniority_module.normalize("CEO")
    # Phải nêu ĐÚNG giá trị bị loại: log "seniority không hợp lệ" trơ trọi thì không truy được ai
    # đang gửi sai — mà đây là fail-open, không có lỗi nào khác để lần theo.
    assert any("CEO" in r.getMessage() for r in caplog.records)


# ══════════════════════════════════════════════════════════════════════════════
# 4. PROMPT — thật sự đổi theo cấp độ, và KHÔNG đổi khi không truyền
# ══════════════════════════════════════════════════════════════════════════════

def _prompt(**kwargs) -> str:
    return build_prompt("BE", None, None, 5, **kwargs)


def test_prompt_senior_khac_fresher():
    """Nhận tham số mà không dùng = hỏng y hệt như không nhận."""
    assert _prompt(seniority="Senior") != _prompt(seniority="Fresher")


@pytest.mark.parametrize("level", LEVELS)
def test_prompt_neu_dich_danh_cap_do_da_chon(level):
    p = _prompt(seniority=level)
    assert f"CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: {level}" in p
    # Có nêu tên cấp độ thôi thì chưa đủ — phải có mô tả để mô hình biết mức đó NGHĨA LÀ GÌ.
    assert "hiệu chỉnh ĐÚNG TẦM" in p


def test_prompt_gia_tri_la_ha_ve_junior_khong_nem():
    assert _prompt(seniority="CEO") == _prompt(seniority="Junior")
    # Chuỗi RỖNG là một giá trị SAI đã được gửi (≠ không gửi) ⇒ vẫn vào nhánh chuẩn hoá.
    assert _prompt(seniority="") == _prompt(seniority="Junior")


def test_prompt_khong_truyen_thi_giu_nguyen_xi():
    """🔒 BẤT BIẾN QUAN TRỌNG NHẤT: không truyền ⇒ prompt không thêm MỘT CHỮ nào.

    `""` khác `None`: rỗng là giá trị sai đã gửi (→ Junior, CÓ khối), None là không gửi (→ không
    có khối). Gộp hai ca này là cách kinh điển làm tính năng vô hiệu ở đúng nhóm cần nó.
    """
    base = _prompt()
    assert base == _prompt(seniority=None)
    assert "CẤP ĐỘ ỨNG VIÊN" not in base
    # Đối chứng DƯƠNG: nếu bản có seniority cũng bằng `base` thì phép trên vô nghĩa.
    assert base != _prompt(seniority="Junior")


def test_prompt_seniority_nam_truoc_khoi_du_lieu_cv_jd():
    """AI-4: chỉ thị của hệ thống PHẢI đứng trước phần DỮ LIỆU của ứng viên/HR.

    Đảo thứ tự = đặt một chỉ thị hệ thống vào sau vùng người dùng kiểm soát được nội dung.
    """
    p = build_prompt("BE", "CV của tôi", "JD tuyển dụng", 5, seniority="Senior")
    assert p.index("CẤP ĐỘ ỨNG VIÊN") < p.index("CHỐNG PROMPT INJECTION")
    assert p.index("CẤP ĐỘ ỨNG VIÊN") < p.index("---CV (DỮ LIỆU")


# ══════════════════════════════════════════════════════════════════════════════
# 5. SỐNG SÓT QUA LƯỢT VIẾT LẠI (SC1c) — chỗ dễ đánh rơi nhất
# ══════════════════════════════════════════════════════════════════════════════

@pytest.mark.asyncio
async def test_seniority_con_nguyen_o_luot_sinh_lai(monkeypatch):
    """Cổng chất lượng SC1c gọi ĐỆ QUY `generate` — trước SEN1 lời gọi đó truyền POSITIONAL.

    Chèn một tham số vào giữa chữ ký `generate` sẽ khiến `_retry_feedback` lặng lẽ rơi vào ô tham số
    mới: lượt viết lại vẫn chạy, vẫn 200, chỉ là mất sạch nhận xét sửa bài VÀ mất cấp độ. Không lỗi
    nào nổ. Test này khoá cả hai vế trên ĐÚNG lượt thứ hai.
    """
    from types import SimpleNamespace
    import json as _json

    from app.config import settings as _settings
    from app.providers.gemini import GeminiProvider

    c1 = "11111111-1111-1111-1111-111111111111"
    c2 = "22222222-2222-2222-2222-222222222222"

    class Models:
        def __init__(self):
            self.calls = 0
            self.prompts: list[str] = []

        async def generate_content(self, *, model, contents, config):
            self.calls += 1
            self.prompts.append(contents)
            # Lượt 1 phủ thiếu (2 câu cùng nhắm c1) ⇒ ép sinh lại; lượt 2 phủ đủ.
            targets = [[c1], [c2]] if self.calls == 2 else [[c1], [c1]]
            return SimpleNamespace(text=_json.dumps(
                {"questions": [{"text": "Q1?", "targetCriterionIds": targets[0]},
                               {"text": "Q2?", "targetCriterionIds": targets[1]}]}))

    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider()
    models = Models()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=models))
    monkeypatch.setattr(_settings, "question_max_attempts", 2)

    await provider.generate(
        "BE", None, None, count=2,
        criteria=[{"criterionId": c1, "name": "Kỹ thuật"}, {"criterionId": c2, "name": "Thiết kế"}],
        seniority="Senior")

    assert models.calls == 2, "phải có lượt sinh lại thì test mới đo được điều nó muốn đo"
    assert "CẤP ĐỘ ỨNG VIÊN DO NGƯỜI DÙNG CHỌN: Senior" in models.prompts[1]
    # Vế thứ hai: nhận xét sửa bài KHÔNG được rơi mất vì tham số mới chen vào.
    assert "Thiết kế" in models.prompts[1]


def test_prompt_khong_lan_mo_ta_cap_do_khac_thanh_chi_thi():
    """Bảng 4 mức được liệt kê hết trong prompt, nên PHẢI có câu chốt 'chỉ áp dụng dòng của bạn'.

    Thiếu câu đó, mô hình đọc cả 4 dòng như 4 chỉ thị ngang nhau ⇒ hiệu chỉnh mất tác dụng mà
    không có triệu chứng nào ngoài 'câu hỏi lệch tầm'.
    """
    p = _prompt(seniority="Fresher")
    assert "CHỈ áp dụng dòng ứng với cấp độ Fresher" in p
