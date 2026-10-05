# tests/test_decide_next_transcription_noise.py — AI hỏi lại CÂU CHỮ do máy chép sai.
#
# Bằng chứng (prod, 2026-10-05 — câu hỏi Clarify/FollowUp sinh từ bản chép ngay trước nó):
#   bản chép: "...tôi vẽ bản trình hiện tại bằng bơm..."        (ứng viên nói "BPMN")
#   AI hỏi : "...bạn đã sử dụng 'bản trình hiện tại bằng bơm' để giúp mọi người..."
#   bản chép: "HashMap là một kiểu dữ liệu của lãnh thổng."
#   AI hỏi : "Bạn có thể giải thích rõ hơn "kiểu dữ liệu của lãnh thổng" nghĩa là gì không?"
#
# Nguyên nhân: đề CHẤM đã có luật F12 (lỗi phiên âm là lỗi máy, không trừ điểm) nhưng đề CHỌN CÂU KẾ
# thì không — nó chỉ ghi "đã chuyển từ giọng nói sang văn bản". Ranh giới của bản vá: cấm hỏi về CHỮ,
# không cấm hỏi về Ý.
import json
from unittest.mock import AsyncMock

import pytest

from app.config import settings
from app.prompts import build_decide_next_prompt
from app.providers.gemini import GeminiProvider, _asks_meaning_of_transcript_phrase

_CRITERIA = [{"name": "Phân tích quy trình", "description": "Mô hình hoá quy trình nghiệp vụ"}]
_TRANSCRIPT = ("Trước hết tôi tìm nguyên nhân gốc, rồi tôi vẽ bản trình hiện tại bằng bơm để mọi "
               "người nhìn thấy điểm nghẽn.")
_QUESTION = "Hai phòng ban có quy trình mâu thuẫn, bạn xử lý thế nào?"

_CHAIN = dict(root_question=_QUESTION, current_depth=0, max_depth=3,
              other_topics=["Kể về một yêu cầu bị đổi giữa chừng"])
_FRONTIER = dict(max_depth=0)


def _prompt(mode: dict) -> str:
    return build_decide_next_prompt(
        job_category="BA", current_question=_QUESTION, transcript=_TRANSCRIPT,
        history=[{"question": _QUESTION, "answer": _TRANSCRIPT, "kind": "Seed"}],
        asked_count=1, follow_up_count=0, max_questions=10, max_follow_ups=3,
        criteria=_CRITERIA, **mode)


@pytest.mark.parametrize("mode", [_CHAIN, _FRONTIER], ids=["chain", "frontier"])
def test_de_bai_noi_ban_chep_la_cua_may(mode):
    prompt = _prompt(mode)

    assert "BẢN CHÉP DO MÁY NHẬN GIỌNG NÓI — KHÔNG HỎI VỀ CÂU CHỮ" in prompt
    assert "LỖI CỦA MÁY, không phải của ứng viên" in prompt


@pytest.mark.parametrize("mode", [_CHAIN, _FRONTIER], ids=["chain", "frontier"])
def test_de_bai_cam_hoi_giai_thich_hay_chep_lai_cum_tu_sai(mode):
    prompt = _prompt(mode)

    assert "TUYỆT ĐỐI KHÔNG hỏi ứng viên giải thích, nhắc lại hay đánh vần" in prompt
    assert "KHÔNG chép nguyên cụm từ trông sai đó vào câu hỏi" in prompt
    assert "Không hỏi về chính tả, ngữ pháp hay cách dùng từ" in prompt


@pytest.mark.parametrize("mode", [_CHAIN, _FRONTIER], ids=["chain", "frontier"])
def test_cum_tu_vo_nghia_mac_dinh_la_may_nghe_nham(mode):
    """Ca "kiểu dữ liệu của lãnh thổng": bản đầu chỉ nói chung "lỗi phiên âm là của máy", Gemini thật
    vẫn hỏi nghĩa cụm đó 4/4 lần — nó không xếp cụm đó vào loại lỗi máy. Phải nói rõ mặc định."""
    prompt = _prompt(mode)

    assert "MẶC ĐỊNH là máy nghe nhầm" in prompt


@pytest.mark.parametrize("mode", [_CHAIN, _FRONTIER], ids=["chain", "frontier"])
def test_yeu_cau_dau_ra_cam_hoi_nghia_cua_cum_tu(mode):
    """Luật nhắc lại SÁT khối JSON — chỉ dặn ở đoạn giữa đề thì lượt thử thật vẫn hỏi nghĩa cụm từ."""
    prompt = _prompt(mode)
    tail = prompt[prompt.index("YÊU CẦU:"):]

    assert "nextQuestion KHÔNG được hỏi nghĩa của một từ/cụm từ trông sai trong bản chép" in tail
    assert "hỏi về NỘI DUNG mà câu hỏi đang cần" in tail


@pytest.mark.parametrize("mode", [_CHAIN, _FRONTIER], ids=["chain", "frontier"])
def test_clarify_chi_vi_y_khong_vi_chu(mode):
    """Định nghĩa `clarify` cũ ("câu trả lời chưa rõ") là chỗ mô hình bám vào để hỏi về một chữ lạ."""
    prompt = _prompt(mode)

    assert ('"clarify": Ý của câu trả lời chưa rõ / thiếu / mơ hồ (KHÔNG phải vì chữ trong bản chép'
            " trông lạ)") in prompt
    assert '"clarify": câu trả lời chưa rõ / thiếu ý / mơ hồ' not in prompt


@pytest.mark.parametrize("mode", [_CHAIN, _FRONTIER], ids=["chain", "frontier"])
def test_luat_nam_o_vung_chi_dan_khong_nam_trong_vung_du_lieu(mode):
    """Đặt luật bên trong khối bọc bản chép/lịch sử thì chính đề bài coi nó là DỮ LIỆU (AI-4)."""
    prompt = _prompt(mode)

    rule = prompt.index("BẢN CHÉP DO MÁY NHẬN GIỌNG NÓI — KHÔNG HỎI VỀ CÂU CHỮ")
    assert rule > prompt.index("---HẾT LỊCH SỬ---")
    assert rule < prompt.index("YÊU CẦU:")


def test_ban_chep_van_nguyen_van_trong_vung_du_lieu():
    """Bản vá không được 'sửa hộ' bản chép — mô hình phải tự đoán theo ngữ cảnh."""
    prompt = _prompt(_CHAIN)

    start = prompt.index("---CÂU TRẢ LỜI MỚI NHẤT")
    end = prompt.index("---HẾT CÂU TRẢ LỜI---")
    assert _TRANSCRIPT in prompt[start:end]


# ── CHỐT CHẶN PHÍA CODE ──────────────────────────────────────────────────────
# Luật prompt (cả bản đã siết) KHÔNG đủ: đo trên aiapi-dev với Gemini thật, ca "lãnh thổng" vẫn ra
# đúng câu hỏi nghĩa 4/4 lượt. Câu kế hỏi NGHĨA của một cụm chép nguyên từ bản chép thì bị trả lại.
_HASHMAP = ("HashMap là một kiểu dữ liệu của lãnh thổng. Tôi sử dụng HashMap trong trường hợp cần "
            "xuất dữ liệu theo dạng KeyValue.")
_MEMO = ("Em đo lại bằng Profiler sau khi sửa để chắc là có cải thiện, vì Memo bừa bãi đôi khi còn "
         "chậm hơn.")
_HASHMAP_Q = "Hash map là gì? Bạn sẽ sử dụng Hash map trong tình huống nào khi xây dựng một API?"


@pytest.mark.parametrize("question", [
    'Bạn có thể giải thích rõ hơn "kiểu dữ liệu của lãnh thổng" nghĩa là gì không?',   # nguyên văn prod
    'Bạn có thể giải thích rõ hơn "kiểu dữ liệu của lãnh thổng" là gì không?',          # lượt thử thật
    "Ý bạn là gì khi nói 'kiểu dữ liệu của lãnh thổng'?",
    "Bạn có thể giải thích “kiểu dữ liệu của lãnh thổng” có nghĩa gì không?",
])
def test_bat_cau_hoi_nghia_cua_cum_chep_tu_ban_chep(question):
    assert _asks_meaning_of_transcript_phrase(question, _HASHMAP, []) == "kiểu dữ liệu của lãnh thổng"


def test_cum_nam_trong_cau_tra_loi_cu_cung_bi_bat():
    history = [{"question": _HASHMAP_Q, "answer": _HASHMAP, "kind": "Seed"}]
    q = 'Bạn có thể giải thích "kiểu dữ liệu của lãnh thổng" nghĩa là gì không?'

    assert _asks_meaning_of_transcript_phrase(q, "Tôi không chắc lắm.", history) is not None


@pytest.mark.parametrize("question, transcript", [
    # Trích lời ứng viên để đào sâu Ý — câu prod hợp lệ, phải đi qua.
    ('Bạn có thể giải thích rõ hơn về việc "Memo bừa bãi đôi khi còn chậm hơn" không?', _MEMO),
    # Có "là gì" nhưng ở một mệnh đề khác, xa cụm trích.
    ('Bạn có thể giải thích rõ hơn về việc "Memo bừa bãi đôi khi còn chậm hơn" không? Trong trường '
     "hợp nào thì useMemo là gì?", _MEMO),
    # Người phỏng vấn tự đưa thuật ngữ — không có trong bản chép.
    ("'Idempotent' là gì và nó áp dụng thế nào cho HashMap?", _HASHMAP),
    # Cụm NHIỀU chữ nhưng không có trong bản chép — vẫn là thuật ngữ do người phỏng vấn đưa ra.
    ("'Eventual consistency' là gì và bạn áp dụng nó ở đâu?", _HASHMAP),
    # Một chữ đơn lẻ: quá dễ trùng ngẫu nhiên, không xét.
    ("'HashMap' là gì?", _HASHMAP),
    ("HashMap xử lý va chạm khoá như thế nào?", _HASHMAP),
])
def test_khong_chan_cau_hoi_hop_le(question, transcript):
    assert _asks_meaning_of_transcript_phrase(question, transcript, []) is None


def _fake(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


async def _decide(provider):
    return await provider.decide_next(
        job_category="BE", current_question=_HASHMAP_Q, transcript=_HASHMAP,
        history=[{"question": _HASHMAP_Q, "answer": _HASHMAP, "kind": "Seed"}],
        asked_count=3, follow_up_count=0, max_questions=20, max_follow_ups=0,
        criteria=[{"name": "Chiều sâu kỹ thuật"}], root_question=_HASHMAP_Q, current_depth=0,
        max_depth=1, other_topics=[])


_BAD = 'Bạn có thể giải thích rõ hơn "kiểu dữ liệu của lãnh thổng" nghĩa là gì không?'
_GOOD = "HashMap xử lý va chạm khoá như thế nào và độ phức tạp tra cứu là bao nhiêu?"


@pytest.mark.asyncio
async def test_cau_hoi_nghia_bi_tra_lai_kem_ly_do(monkeypatch):
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    gen = provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "clarify", "nextQuestion": _BAD}),
        _fake({"action": "follow_up", "nextQuestion": _GOOD}),
    ])

    result = await _decide(provider)

    assert result["nextQuestion"] == _GOOD
    assert gen.await_count == 2
    de_lan_2 = gen.await_args_list[1].kwargs["contents"]
    loi_tra_lai = de_lan_2[de_lan_2.index("BỊ TRẢ LẠI"):]
    # Nêu ĐÚNG cụm bị cấm. Chỉ kiểm cụm có trong cả đề thì luôn đúng — bản chép đã chứa nó sẵn.
    assert "cụm 'kiểu dữ liệu của lãnh thổng'" in loi_tra_lai
    assert "máy nghe nhầm" in loi_tra_lai


@pytest.mark.asyncio
async def test_can_luot_van_hoi_nghia_thi_dong_chuoi_khong_tra_cau_do(monkeypatch):
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "clarify", "nextQuestion": _BAD}),
        _fake({"action": "clarify", "nextQuestion": _BAD}),
    ])

    result = await _decide(provider)

    assert result["action"] == "end"
    assert result["nextQuestion"] is None
    assert "máy nghe nhầm" in result["reason"]


@pytest.mark.asyncio
async def test_cau_dao_sau_y_hop_le_di_qua_ngay_luot_dau(monkeypatch):
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    ok = 'Bạn có thể giải thích rõ hơn về việc "Memo bừa bãi đôi khi còn chậm hơn" không?'
    gen = provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "follow_up", "nextQuestion": ok}),
    ])

    result = await provider.decide_next(
        job_category="FE", current_question="Bạn chẩn đoán re-render thế nào?", transcript=_MEMO,
        history=[], asked_count=3, follow_up_count=0, max_questions=20, max_follow_ups=0,
        criteria=[{"name": "Chiều sâu kỹ thuật"}], root_question="Bạn chẩn đoán re-render thế nào?",
        current_depth=0, max_depth=1, other_topics=[])

    assert result["nextQuestion"] == ok
    assert gen.await_count == 1
