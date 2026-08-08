import asyncio
import json
from types import SimpleNamespace
import pytest

from app.providers.gemini import GeminiProvider
from app.question_quality import coverage_defects
from app.config import settings

C1 = "11111111-1111-1111-1111-111111111111"
C2 = "22222222-2222-2222-2222-222222222222"
C3 = "33333333-3333-3333-3333-333333333333"

class Models:
    def __init__(self): self.calls = 0; self.prompts = []
    async def generate_content(self, *, model, contents, config):
        self.calls += 1; self.prompts.append(contents)
        payload = ({"questions":[{"text":"Q1?","targetCriterionIds":[C1]}, {"text":"Q2?","targetCriterionIds":[C2]}]}
                   if self.calls == 2 else {"questions":[{"text":"Q1?","targetCriterionIds":[C1]}, {"text":"Q2?","targetCriterionIds":[C1]}]})
        return SimpleNamespace(text=json.dumps(payload))

@pytest.mark.asyncio
async def test_missing_coverage_retries_once_with_named_feedback(monkeypatch):
    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider(); models = Models()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=models))
    monkeypatch.setattr(settings, "question_max_attempts", 2)
    result = await provider.generate("BE", None, None, count=2, criteria=[
        {"criterionId": C1, "name":"Kỹ thuật"}, {"criterionId": C2, "name":"Thiết kế"}])
    assert models.calls == 2
    assert "Thiết kế" in models.prompts[1]
    assert result.target_criteria == [[C1], [C2]]


@pytest.mark.asyncio
async def test_verify_uses_grounding_for_citations_without_injecting_it_into_generation(monkeypatch):
    class VerifyModels:
        def __init__(self): self.calls = []
        async def generate_content(self, *, model, contents, config):
            self.calls.append(contents)
            if len(self.calls) == 1:
                return SimpleNamespace(text=json.dumps({"questions": [{"text": "Q?", "citedChunkIds": []}]}))
            return SimpleNamespace(text=json.dumps({"checks": [{"questionIndex": 0, "citedChunkIds": ["c1"], "reason": None}]}))
    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider(); models = VerifyModels()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=models))
    monkeypatch.setattr(settings, "question_verify_enabled", True)
    result = await provider.generate("BE", None, None, count=1,
                                     grounding=[{"chunkId": "c1", "content": "Nguồn đúng"}])
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" not in models.calls[0]
    assert result.citations == [{"questionIndex": 0, "citedChunkIds": ["c1"]}]


# ══════════════════════════════════════════════════════════════════════════════
# Ít câu hơn tiêu chí — bản kiểm PHẢI khớp nhánh prompt, không đòi phủ 100%
#
# Vì sao đây là bug TIỀN chứ không phải nit: `build_prompt` khi count < n đã bảo model "Chỉ có {count}
# câu hỏi cho {n} tiêu chí, nên hãy chọn {count} tiêu chí KHÁC NHAU". Model làm ĐÚNG thứ được yêu cầu,
# bản kiểm cũ vẫn gọi là khiếm khuyết ⇒ sinh lại kèm nhận xét mâu thuẫn với chính đề bài ⇒ lượt 2 cũng
# không phủ nổi ⇒ giao hàng. 100% số lần mất một lượt Gemini, thu về 0.
# ══════════════════════════════════════════════════════════════════════════════

def _criteria3() -> list[dict]:
    return [{"criterionId": C1, "name": "Kỹ thuật"},
            {"criterionId": C2, "name": "Thiết kế"},
            {"criterionId": C3, "name": "Thuật toán"}]


def test_it_cau_hon_tieu_chi_moi_cau_mot_tieu_chi_rieng_la_DAT():
    """2 câu / 3 tiêu chí, hai nhãn khác nhau = đúng thứ prompt đòi ⇒ KHÔNG được coi là khiếm khuyết."""
    assert coverage_defects([[C1], [C2]], _criteria3(), 2) == []


def test_it_cau_hon_tieu_chi_nhung_don_cuc_van_la_khiem_khuyet():
    """…còn dồn hai câu vào cùng một tiêu chí thì vẫn phải trả lại: đó là khe bị phí, và model SỬA ĐƯỢC."""
    defects = coverage_defects([[C1], [C1]], _criteria3(), 2)
    assert len(defects) == 1
    assert "2 tiêu chí KHÁC NHAU" in defects[0]


def test_du_cau_thi_van_doi_phu_du():
    """Biên count == n: vẫn là nhánh phủ-đủ (đây mới là ca SC1 đã đo trên prod)."""
    defects = coverage_defects([[C1], [C1], [C2]], _criteria3(), 3)
    assert len(defects) == 1 and "Thuật toán" in defects[0]
    assert coverage_defects([[C1], [C2], [C3]], _criteria3(), 3) == []


def test_khong_biet_count_thi_giu_nhanh_phu_du():
    """`count=None` = caller cũ ⇒ giữ NGUYÊN hành vi trước (fail-safe, không âm thầm nới lỏng)."""
    assert coverage_defects([[C1], [C2]], _criteria3()) != []


def test_nhan_xet_theo_ngon_ngu_buoi(monkeypatch):
    """Q10 — nhận xét đi thẳng vào prompt lượt sinh lại; buổi tiếng Anh mà nhận xét tiếng Việt là ra đề
    bằng hai thứ tiếng."""
    monkeypatch.setenv("BILINGUAL_ALLOWED_LANGUAGES", "vi,en")
    vi = coverage_defects([[C1]], _criteria3(), 3)[0]
    en = coverage_defects([[C1]], _criteria3(), 3, language="en")[0]
    assert "Các tiêu chí sau chưa có câu hỏi nào nhắm tới" in vi
    assert "No question targets these criteria yet" in en
    assert "Hãy sửa NỘI DUNG" not in en

    vi2 = coverage_defects([[C1], [C1]], _criteria3(), 2)[0]
    en2 = coverage_defects([[C1], [C1]], _criteria3(), 2, language="en")[0]
    assert "tiêu chí KHÁC NHAU" in vi2
    assert "DIFFERENT criteria" in en2


@pytest.mark.asyncio
async def test_it_cau_hon_tieu_chi_KHONG_sinh_lai_khi_da_dat(monkeypatch):
    """Phép đo đắt nhất của nhóm này: đếm SỐ LƯỢT GỌI GEMINI, không chỉ đọc kết quả.

    Trước bản vá, đường này gọi Gemini 2 lần MỖI LẦN sinh câu hỏi cho buổi có ít câu hơn tiêu chí."""
    class Fake:
        def __init__(self): self.calls = 0
        async def generate_content(self, *, model, contents, config):
            self.calls += 1
            return SimpleNamespace(text=json.dumps({"questions": [
                {"text": "Q1?", "targetCriterionIds": [C1]},
                {"text": "Q2?", "targetCriterionIds": [C2]}]}))

    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider(); fake = Fake()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=fake))
    monkeypatch.setattr(settings, "question_max_attempts", 2)

    result = await provider.generate("BE", None, None, count=2, criteria=_criteria3())

    assert fake.calls == 1
    assert result.target_criteria == [[C1], [C2]]


@pytest.mark.asyncio
async def test_nhan_xet_gui_vao_prompt_sinh_lai_theo_dung_ngon_ngu(monkeypatch):
    """Ngôn ngữ của buổi phải đi HẾT đường tới prompt lượt 2, không dừng ở hàm thuần."""
    class Fake:
        def __init__(self): self.prompts = []
        async def generate_content(self, *, model, contents, config):
            self.prompts.append(contents)
            return SimpleNamespace(text=json.dumps({"questions": [
                {"text": "Q1?", "targetCriterionIds": [C1]},
                {"text": "Q2?", "targetCriterionIds": [C1]}]}))

    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider(); fake = Fake()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=fake))
    monkeypatch.setattr(settings, "question_max_attempts", 2)
    monkeypatch.setenv("BILINGUAL_ALLOWED_LANGUAGES", "vi,en")

    await provider.generate("BE", None, None, count=2, criteria=_criteria3(), language="en")

    assert len(fake.prompts) == 2
    # Chỉ soi KHỐI NHẬN XÉT: phần còn lại của prompt vốn hardcode tiếng Việt (thiết kế sẵn có — model
    # được dặn TRẢ LỜI bằng tiếng Anh qua `output_directive`), nên assert cả prompt sẽ đo nhầm thứ.
    feedback_block = fake.prompts[1].split("NHẬN XÉT BẮT BUỘC TỪ LƯỢT TRƯỚC")[1]
    assert "DIFFERENT criteria" in feedback_block
    assert "Mới có" not in feedback_block          # câu chữ tiếng Việt của cùng khiếm khuyết
