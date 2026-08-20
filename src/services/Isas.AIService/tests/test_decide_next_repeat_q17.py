# tests/test_decide_next_repeat_q17.py — Q17: câu đào sâu TRÙNG KHÍT câu vừa hỏi.
#
# Bằng chứng (prod, một buổi trong DB — order · kind · depth):
#   1 · Seed    · 0 — "Trong dự án xây dựng hệ thống microservice xử lý 10.000 request…"
#   2 · Clarify · 1 — "Bạn có thể chia sẻ cụ thể hơn về cách bạn đã thiết kế và triển khai cá…"
#   3 · Clarify · 2 — TRÙNG KHÍT TỪNG CHỮ câu 2
# Cả ba nhận CÙNG một bản chép ("Tôi từng làm việc với các API Jestful và cơ sở dữ liệu…").
# **10 buổi** trong DB có câu trùng khít từng chữ trong cùng một session.
#
# Nguyên nhân: prompt cũ chỉ có ĐÚNG MỘT luật chống trùng (khối `other_topics`) và nó chỉ chặn đụng
# sang các câu GỐC KHÁC. Câu trả lời rỗng nội dung là đầu vào BẤT ĐỘNG cho `clarify` ("chưa rõ →
# hỏi làm rõ chính ý đó") ⇒ sinh lại gần y hệt, mãi mãi.
#
# Quyết định sản phẩm: LÀM RÕ MỘT LẦN rồi chuyển chủ đề. Ba nhóm test theo ba lớp vá:
#   1. NGUỒN — đề bài cấm hỏi lại + luật "clarify một lần" + ca "không biết/im lặng" = ĐÓNG.
#   2. SO SÁNH — `_normalize_question` / `_is_repeat_question`: chuẩn hoá NHẸ, không fuzzy.
#   3. CHỐT CHẶN — trùng thì trả lại kèm lý do (Q16); cạn lượt thì `end`, KHÔNG trả câu trùng.
import json
from unittest.mock import AsyncMock

import pytest

from app.config import settings
from app.prompts import build_decide_next_prompt
from app.providers.gemini import (GeminiProvider, _is_repeat_question, _normalize_question)

_CRITERIA = [{"name": "Kiến thức kỹ thuật", "description": "Hiểu khái niệm cốt lõi"}]

# Chính cặp câu đã trùng trên prod (rút gọn cho vừa dòng, giữ nguyên phần đầu).
_CLARIFY_1 = ("Bạn có thể chia sẻ cụ thể hơn về cách bạn đã thiết kế và triển khai các API đó "
              "không?")
# Bản chép của câu trả lời — rỗng nội dung kiểm chứng được, đúng thứ làm `clarify` lặp vô hạn.
_ANSWER_RONG = "Tôi từng làm việc với các API Jestful và cơ sở dữ liệu."

_CHAIN = dict(root_question="Trong dự án microservice 10.000 request, bạn đã làm gì?",
              current_depth=1, max_depth=3, other_topics=["Kể về một bug khó"])


def _fake(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


async def _decide(provider, **over):
    kwargs = dict(job_category="BE", current_question=_CLARIFY_1, transcript=_ANSWER_RONG,
                  history=[{"question": "Trong dự án microservice 10.000 request, bạn đã làm gì?",
                            "answer": _ANSWER_RONG, "kind": "Seed"}],
                  asked_count=2, follow_up_count=1, max_questions=10, max_follow_ups=3,
                  criteria=_CRITERIA, **_CHAIN)
    kwargs.update(over)
    return await provider.decide_next(**kwargs)


# ── 1. NGUỒN: đề bài phải NÓI luật, không để mô hình tự đoán ────────────────
def _prompt(**over):
    kwargs = dict(job_category="BE", current_question=_CLARIFY_1, transcript=_ANSWER_RONG,
                  history=[{"question": _CLARIFY_1, "answer": _ANSWER_RONG, "kind": "Clarify"}],
                  asked_count=2, follow_up_count=1, max_questions=10, max_follow_ups=3,
                  criteria=_CRITERIA, **_CHAIN)
    kwargs.update(over)
    return build_decide_next_prompt(**kwargs)


def test_de_bai_cam_hoi_lai_cau_da_hoi():
    """Trước bản vá, luật chống trùng DUY NHẤT là `other_topics` — chỉ chặn đụng sang câu GỐC KHÁC,
    không có chữ nào cấm hỏi lại chính câu vừa hỏi trong CÙNG chuỗi."""
    prompt = _prompt()

    assert "KHÔNG HỎI LẠI CÂU ĐÃ HỎI" in prompt
    assert "CÂU HỎI HIỆN TẠI" in prompt and "bất kỳ câu nào trong phần lịch sử" in prompt


def test_de_bai_noi_lam_ro_chi_mot_lan():
    prompt = _prompt()

    assert "LÀM RÕ CHỈ MỘT LẦN" in prompt
    assert "TUYỆT ĐỐI không clarify lần thứ hai" in prompt


def test_de_bai_coi_khong_biet_va_im_lang_la_tin_hieu_dong():
    """Ca gốc của lỗi: ứng viên không có gì thêm để nói. Không nói rõ thì mô hình đọc đó là "chưa
    rõ → làm rõ", tức đúng cái nhánh sinh ra vòng lặp."""
    prompt = _prompt()

    assert "tín hiệu ĐÓNG LẠI, KHÔNG phải tín hiệu hỏi lại" in prompt
    for signal in ("không biết", "im lặng", "lặp lại gần y nguyên"):
        assert signal in prompt


def test_loi_thoat_khac_nhau_theo_che_do():
    """`end` mang hai nghĩa khác nhau: chuỗi → hết CHỦ ĐỀ (rẻ) · chế độ cũ → hết BUỔI (đắt). Bảo
    chế độ cũ `end` khi ứng viên bí một câu là cắt ngang cả buổi phỏng vấn."""
    chain = _prompt()
    assert 'chọn action = "end" để đóng chủ đề này' in chain

    legacy = _prompt(root_question=None, current_depth=0, max_depth=0, other_topics=None)
    assert 'chuyển sang năng lực khác bằng action = "new_question"' in legacy
    assert "đóng chủ đề này" not in legacy


def test_luat_chong_trung_ap_cho_ca_hai_che_do():
    """`max_depth = 0` giữ nguyên văn prompt cũ về mặt INT-17b (không có chủ đề/tầng), nhưng lỗi
    hỏi-lại KHÔNG phải đặc sản của chế độ chuỗi — luật này phải có ở cả hai."""
    legacy = _prompt(root_question=None, current_depth=0, max_depth=0, other_topics=None)

    assert "KHÔNG HỎI LẠI CÂU ĐÃ HỎI" in legacy
    assert "LÀM RÕ CHỈ MỘT LẦN" in legacy


# ── 2. SO SÁNH: chuẩn hoá NHẸ, cố ý không fuzzy ────────────────────────────
@pytest.mark.parametrize("variant", [
    _CLARIFY_1,
    _CLARIFY_1.upper(),                                  # hoa/thường
    f"  {_CLARIFY_1}  ",                                 # khoảng trắng đầu/cuối
    _CLARIFY_1.replace(" ", "  "),                       # khoảng trắng thừa giữa câu
    _CLARIFY_1.replace(" đã ", "\n đã \t"),              # xuống dòng/tab từ output model
    _CLARIFY_1.rstrip("?") + ".",                        # đổi dấu câu cuối
    _CLARIFY_1.rstrip("?"),                              # mất hẳn dấu câu cuối
])
def test_chuan_hoa_nhe_van_bat_duoc_trung(variant):
    assert _normalize_question(variant) == _normalize_question(_CLARIFY_1)


def test_khong_so_ngu_nghia_va_khong_bo_dau():
    """Chốt tường minh ranh giới: hai câu KHÁC NỘI DUNG trong cùng chủ đề không được coi là trùng,
    kể cả khi chúng chia nhau phần lớn số chữ. Bắt nhầm ở đây tốn đúng thứ bản vá đang cứu."""
    gan_giong = "Bạn có thể chia sẻ cụ thể hơn về cách bạn đã kiểm thử các API đó không?"

    assert _normalize_question(gan_giong) != _normalize_question(_CLARIFY_1)
    assert _is_repeat_question(gan_giong, _CLARIFY_1, []) is False


def test_bat_trung_voi_cau_dang_hoi_va_voi_lich_su():
    history = [{"question": "Trong dự án microservice 10.000 request, bạn đã làm gì?",
                "answer": _ANSWER_RONG, "kind": "Seed"}]

    assert _is_repeat_question(_CLARIFY_1, _CLARIFY_1, history) is True          # câu đang hỏi
    assert _is_repeat_question(history[0]["question"], _CLARIFY_1, history) is True  # câu gốc
    assert _is_repeat_question("Bạn đo p99 latency bằng gì?", _CLARIFY_1, history) is False


def test_cau_rong_khong_bi_bao_la_trung():
    """Rỗng/toàn dấu câu là việc của `_looks_truncated` (Q16). Báo "trùng" ở đây sẽ đổi kết cục từ
    502-degrade sang `end` cho một hình dạng hỏng chẳng liên quan."""
    assert _is_repeat_question("", _CLARIFY_1, []) is False
    assert _is_repeat_question("   ", _CLARIFY_1, []) is False
    assert _is_repeat_question("...", "...", []) is False


def test_lich_su_khuyet_question_khong_lam_no():
    """History do caller truyền (stateless). Một lượt khuyết `question` không được đánh hỏng cả
    lượt quyết định."""
    assert _is_repeat_question("Bạn đo p99 bằng gì?", _CLARIFY_1,
                               [{"answer": "x"}, {"question": None}, {"question": "  "}]) is False


# ── 3. CHỐT CHẶN: trả lại (Q16) rồi đóng chuỗi ─────────────────────────────
@pytest.mark.asyncio
async def test_cau_trung_bi_tra_lai_va_hoi_lai_kem_ly_do(monkeypatch):
    """Dùng ĐÚNG cơ chế Q16 sẵn có: `retry_feedback` + `decide_next_max_attempts`. Đề lượt 2 phải
    NÊU ĐÚNG chỗ hỏng, hỏi lại y hệt đề cũ thì phần lớn nhận lại đúng cái sai đó."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "clarify", "nextQuestion": _CLARIFY_1}),
        _fake({"action": "follow_up", "nextQuestion": "Bạn đo p99 latency bằng công cụ nào?"}),
    ])

    result = await _decide(provider)

    assert result["nextQuestion"] == "Bạn đo p99 latency bằng công cụ nào?"
    assert provider._client.aio.models.generate_content.await_count == 2

    de_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    assert "BỊ TRẢ LẠI" in de_lan_2
    assert "trùng một câu đã hỏi" in de_lan_2
    assert _CLARIFY_1 in de_lan_2          # nêu đúng câu hỏng, không nuốt


@pytest.mark.asyncio
async def test_trung_voi_cau_trong_lich_su_cung_bi_chan(monkeypatch):
    """Ca prod đúng nghĩa đen: câu 3 trùng khít câu 2, mà câu 2 lúc đó đã nằm trong lịch sử."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "clarify", "nextQuestion": _CLARIFY_1}),
        _fake({"action": "follow_up", "nextQuestion": "Bạn xử lý retry thế nào?"}),
    ])

    result = await _decide(
        provider,
        current_question="Bạn dùng message queue nào?",
        history=[{"question": _CLARIFY_1, "answer": _ANSWER_RONG, "kind": "Clarify"}])

    assert result["nextQuestion"] == "Bạn xử lý retry thế nào?"
    assert provider._client.aio.models.generate_content.await_count == 2


@pytest.mark.asyncio
async def test_can_luot_van_trung_thi_dong_chuoi_chu_khong_tra_cau_trung(monkeypatch):
    """Kết cục cuối cùng của ca prod. `end` = hết CHỦ ĐỀ (INT-17b) ⇒ hệ tự chuyển sang câu gốc kế,
    ứng viên không mất lượt. Câu trùng TUYỆT ĐỐI không được đi ra ngoài."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "clarify", "nextQuestion": _CLARIFY_1,
                            "reason": "Câu trả lời còn chung chung."}))

    result = await _decide(provider)

    assert result["action"] == "end"
    assert result["nextQuestion"] is None
    assert _CLARIFY_1 not in json.dumps(result, ensure_ascii=False)
    assert provider._client.aio.models.generate_content.await_count == 2


@pytest.mark.asyncio
async def test_dong_chuoi_giu_nhan_bang_chung_cua_luot_cuoi(monkeypatch):
    """Đề bài yêu cầu `end` VẪN kèm targetCriterionId + trạng thái mới nhất — .NET đang giữ state
    theo tiêu chí, vứt nhãn đi thì lượt này thành lỗ hổng trong state đó."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 1)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "clarify", "nextQuestion": _CLARIFY_1,
                            "targetCriterionId": "c-1", "evidenceFound": ["Có nhắc REST API"],
                            "missingEvidence": ["Chưa có chi tiết thiết kế"],
                            "newEvidenceState": "PARTIAL"}))

    result = await _decide(provider)

    assert result["action"] == "end"
    assert result["targetCriterionId"] == "c-1"
    assert result["newEvidenceState"] == "PARTIAL"
    assert result["missingEvidence"] == ["Chưa có chi tiết thiết kế"]
    assert "trùng" in result["reason"]      # reason nói đúng vì sao đóng, không mượn lý do cũ


@pytest.mark.asyncio
async def test_kill_switch_mot_luot_van_dong_chuoi_ngay(monkeypatch):
    """`decide_next_max_attempts=1` tắt việc thử lại (đường ĐỒNG BỘ, độ trễ là chi phí thật) —
    nhưng chốt chặn vẫn phải giữ: đóng chuỗi ngay, không gọi Gemini lần hai."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 1)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "clarify", "nextQuestion": _CLARIFY_1}))

    result = await _decide(provider)

    assert result["action"] == "end" and result["nextQuestion"] is None
    assert provider._client.aio.models.generate_content.await_count == 1


@pytest.mark.asyncio
async def test_chuan_hoa_hoa_thuong_va_khoang_trang_van_bi_chan(monkeypatch):
    """Đổi hoa/thường + nhân đôi khoảng trắng + đổi dấu câu cuối vẫn là ĐÚNG câu đó."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 1)
    provider = GeminiProvider()
    tra_hinh = "  " + _CLARIFY_1.upper().replace(" ", "  ").rstrip("?") + "!"
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "clarify", "nextQuestion": tra_hinh}))

    result = await _decide(provider)

    assert result["action"] == "end" and result["nextQuestion"] is None


@pytest.mark.asyncio
async def test_cau_khac_khong_bi_chan_nham_va_khong_ton_luot(monkeypatch):
    """Mặt kia của đánh đổi: một câu đào sâu HỢP LỆ trong cùng chủ đề phải đi thẳng, một lượt gọi.
    Chặn nhầm ở đây vừa cướp lượt hỏi của ứng viên vừa tốn thêm tiền + độ trễ."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "follow_up",
                            "nextQuestion": "Bạn có thể chia sẻ cụ thể hơn về cách bạn đã kiểm "
                                            "thử các API đó không?"}))

    result = await _decide(provider)

    assert result["action"] == "follow_up"
    assert result["nextQuestion"].startswith("Bạn có thể chia sẻ cụ thể hơn về cách bạn đã kiểm")
    assert provider._client.aio.models.generate_content.await_count == 1


@pytest.mark.asyncio
async def test_end_khong_bi_soi_trung(monkeypatch):
    """`end` không kèm câu hỏi — soi nó là biến mọi lượt đóng chủ đề thành lượt gọi thừa."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "end", "nextQuestion": "", "reason": "Đủ rồi."}))

    result = await _decide(provider)

    assert result["action"] == "end" and result["reason"] == "Đủ rồi."
    assert provider._client.aio.models.generate_content.await_count == 1


@pytest.mark.asyncio
async def test_hong_kieu_khac_o_luot_cuoi_van_raise_chu_khong_thanh_end(monkeypatch):
    """LỖI CUỐI quyết định kết cục, không phải "đã từng trùng một lần". Trùng ⇒ ta CÓ một quyết
    định dùng được ("chủ đề hết cái để hỏi") nên `end` là đúng; còn câu cụt/JSON hỏng thì ta KHÔNG
    biết gì cả — nuốt nó thành `end` là âm thầm cắt ngắn buổi phỏng vấn vì một lỗi hạ tầng."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "clarify", "nextQuestion": _CLARIFY_1}),          # trùng
        _fake({"action": "clarify", "nextQuestion": "Bạn có thể giải thích rõ hơn về"}),  # cụt
    ])

    with pytest.raises(ValueError) as ex:
        await _decide(provider)

    assert "chưa hoàn chỉnh" in str(ex.value)
