# tests/test_decide_next_completeness_q16.py — Q16: câu đào sâu CỤT không được tới tay ứng viên.
#
# Bằng chứng (deploy 2026-08-07): `practice_questions` có một câu `Clarify` dài 31 ký tự —
# "Bạn có thể giải thích rõ hơn về" — đã trả cho ứng viên qua response upload, ứng viên đã trả lời,
# answer đã `Scored`. Các câu Clarify khác cùng bảng dài 168/177 ký tự và hoàn chỉnh.
#
# Ba lớp vá, ba nhóm test:
#   1. GUARD — `_looks_truncated` kiểm TÍNH TRỌN VẸN, cố ý KHÔNG phải ngưỡng độ dài.
#   2. RETRY — `/decide-next` từng là đường DUY NHẤT của provider không có retry.
#   3. NGUỒN — đề bài bỏ "ngắn gọn" + bỏ placeholder `"..."` (cùng cơ chế đã làm hỏng bài giảng
#      ngày 2026-08-03, repo tự ghi lại ở `build_lesson_theory_prompt`).
import json
from unittest.mock import AsyncMock

import pytest

from app.config import settings
from app.prompts import build_decide_next_prompt
from app.providers.gemini import GeminiProvider, _generation_diagnostics, _looks_truncated

# Chính chuỗi đã lọt ra deploy. Giữ nguyên văn để test nói đúng ca thật, không phải ca dựng.
_PROD_TRUNCATED = "Bạn có thể giải thích rõ hơn về"

_CRITERIA = [{"name": "Kiến thức kỹ thuật", "description": "Hiểu khái niệm cốt lõi"}]


def _fake(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


async def _decide(provider, **over):
    kwargs = dict(job_category="BE", current_question="Giải thích DI?",
                  transcript="DI giúp giảm coupling.", history=[],
                  asked_count=1, follow_up_count=0, max_questions=10, max_follow_ups=3,
                  criteria=_CRITERIA)
    kwargs.update(over)
    return await provider.decide_next(**kwargs)


# ── 1. GUARD: trọn vẹn, KHÔNG phải độ dài ──────────────────────────────────
@pytest.mark.parametrize("question", [
    _PROD_TRUNCATED,
    "Bạn có thể giải thích rõ hơn về",                 # y hệt, để rõ ràng khi đọc lỗi
    "Could you walk me through how you",
    "...",                                             # placeholder trong đề bài cũ bị chép lại
    "…",
    "?",
    "   ",
    # DÀI mà vẫn cụt — chốt rằng guard không phải ngưỡng độ dài trá hình.
    "Trong dự án gần nhất bạn đã dùng Dependency Injection để tách phụ thuộc giữa các tầng, "
    "vậy khi container phải dựng một đối tượng có vòng đời khác với đối tượng cha thì bạn",
])
def test_bat_duoc_cau_cut(question):
    assert _looks_truncated(question) is True


@pytest.mark.parametrize("question", [
    "Bạn dùng index nào?",                             # NGẮN mà trọn vẹn — không được bắt oan
    "Hãy mô tả cách bạn xử lý lỗi.",                   # mệnh lệnh kết bằng dấu chấm
    "Vì sao lại chọn cách đó!",
    "Could you walk me through how you handled that outage?",
    "Bạn có thể nêu ví dụ cụ thể về DI không?  ",      # khoảng trắng thừa không tính
])
def test_khong_bat_oan_cau_tron_ven(question):
    assert _looks_truncated(question) is False


def test_guard_khong_phai_nguong_do_dai():
    """Chốt tường minh điều dễ bị hiểu nhầm nhất về bản vá này: một câu 19 ký tự ĐẠT, một câu 168
    ký tự TRƯỢT. Nếu ai đó sau này thay guard bằng `len(q) < N` thì cặp khẳng định này đỏ."""
    ngan_ma_du = "Bạn dùng index nào?"
    dai_ma_cut = ("Trong dự án gần nhất bạn đã dùng Dependency Injection để tách phụ thuộc giữa "
                  "các tầng, vậy khi container phải dựng một đối tượng có vòng đời khác thì bạn")

    assert len(ngan_ma_du) < len(dai_ma_cut)
    assert _looks_truncated(ngan_ma_du) is False
    assert _looks_truncated(dai_ma_cut) is True


@pytest.mark.asyncio
async def test_cau_cut_khong_bao_gio_duoc_tra_ve(monkeypatch):
    """Ca prod: nếu mô hình cứ trả câu cụt thì cạn lượt → ValueError ⇒ .NET degrade về luồng tĩnh
    (answer VẪN được lưu). Nửa câu KHÔNG được đi tiếp."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "clarify", "nextQuestion": _PROD_TRUNCATED}))

    with pytest.raises(ValueError) as ex:
        await _decide(provider)

    assert "chưa hoàn chỉnh" in str(ex.value)
    assert _PROD_TRUNCATED in str(ex.value)     # lý do phải nêu đúng chuỗi hỏng, không nuốt


@pytest.mark.asyncio
async def test_action_end_khong_bi_guard_soi(monkeypatch):
    """`end` không kèm câu hỏi — guard trọn-vẹn không được áp vào đó, nếu không mọi lượt kết thúc
    chuỗi (INT-17b: `end` = hết CHỦ ĐỀ, xảy ra rất thường xuyên) đều thành 502."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 1)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "end", "nextQuestion": "", "reason": "Đủ rồi."}))

    result = await _decide(provider)

    assert result["action"] == "end" and result["nextQuestion"] is None


# ── 2. RETRY ───────────────────────────────────────────────────────────────
@pytest.mark.asyncio
async def test_cau_cut_duoc_hoi_lai_va_lan_hai_duoc_nhan(monkeypatch):
    """Lượt 1 cụt → trả lại; lượt 2 trọn vẹn → nhận. Đề lượt 2 phải NÊU ĐÚNG chỗ hỏng, chứ hỏi lại
    y hệt thì phần lớn nhận lại đúng cái sai đó (bài học `generate_lesson_theory`)."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "clarify", "nextQuestion": _PROD_TRUNCATED}),
        _fake({"action": "clarify", "nextQuestion": "Bạn có thể giải thích rõ hơn về cách bạn "
                                                    "cấu hình vòng đời của service?"}),
    ])

    result = await _decide(provider)

    assert result["nextQuestion"].endswith("?")
    assert provider._client.aio.models.generate_content.await_count == 2

    prompt_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    assert "BỊ TRẢ LẠI" in prompt_lan_2
    assert "chưa hoàn chỉnh" in prompt_lan_2


@pytest.mark.asyncio
async def test_json_hong_cung_duoc_hoi_lai(monkeypatch):
    """JSON hỏng chợp nhoáng là đúng thứ retry sinh ra để đỡ — cùng lý lẽ AI3 cho `score()`."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    broken = AsyncMock()
    broken.text = '{"action": "clarify", "nextQues'

    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        broken,
        _fake({"action": "clarify", "nextQuestion": "Bạn cấu hình vòng đời service thế nào?"}),
    ])

    result = await _decide(provider)

    assert result["action"] == "clarify"
    assert provider._client.aio.models.generate_content.await_count == 2


@pytest.mark.asyncio
async def test_action_la_cung_duoc_hoi_lai(monkeypatch):
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake({"action": "ask_something_else", "nextQuestion": "Bạn nghĩ sao?"}),
        _fake({"action": "follow_up", "nextQuestion": "Bạn đo hiệu năng bằng công cụ nào?"}),
    ])

    result = await _decide(provider)

    assert result["action"] == "follow_up"


@pytest.mark.asyncio
async def test_dat_mot_luot_la_ve_hanh_vi_cu(monkeypatch):
    """`decide_next_max_attempts=1` = kill-switch: raise ngay lượt đầu, KHÔNG gọi Gemini lần hai.

    Cần khoá vì đây là đường ĐỒNG BỘ trong request upload — ai thấy độ trễ tăng phải tắt được
    việc thử lại mà không cần deploy lại.
    """
    monkeypatch.setattr(settings, "decide_next_max_attempts", 1)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "clarify", "nextQuestion": _PROD_TRUNCATED}))

    with pytest.raises(ValueError):
        await _decide(provider)

    assert provider._client.aio.models.generate_content.await_count == 1


@pytest.mark.asyncio
async def test_luot_dau_dat_thi_khong_goi_lan_hai(monkeypatch):
    """Retry không được biến mọi lượt bình thường thành hai lượt — đó là tiền + độ trễ."""
    monkeypatch.setattr(settings, "decide_next_max_attempts", 2)
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake({"action": "follow_up",
                            "nextQuestion": "Bạn đo hiệu năng bằng công cụ nào?"}))

    await _decide(provider)

    assert provider._client.aio.models.generate_content.await_count == 1


# ── 3. NGUỒN: đề bài không còn mời mô hình viết cụt ────────────────────────
def test_de_bai_khong_con_placeholder_va_khong_giuc_ngan():
    """Cùng cặp tín hiệu đã làm hỏng bài giảng 2026-08-03 ("không quá dài dòng" + khung JSON
    `"..."` → mô hình chép khung, bỏ ruột). Khoá lại để không ai vô tình đưa về."""
    prompt = build_decide_next_prompt(
        job_category="BE", current_question="Q", transcript="t", history=[],
        asked_count=1, follow_up_count=0, max_questions=10, max_follow_ups=3,
        criteria=_CRITERIA)

    assert '"nextQuestion":"..."' not in prompt
    assert '"reason":"..."' not in prompt
    assert "ngắn gọn" not in prompt
    assert "HOÀN CHỈNH" in prompt and "TRẢ LẠI" in prompt


def test_de_bai_mang_nhan_xet_luot_truoc_khi_co():
    prompt = build_decide_next_prompt(
        job_category="BE", current_question="Q", transcript="t", history=[],
        asked_count=1, follow_up_count=0, max_questions=10, max_follow_ups=3,
        criteria=_CRITERIA, retry_feedback="nextQuestion là câu chưa hoàn chỉnh")

    assert "LƯỢT TRƯỚC CỦA BẠN BỊ TRẢ LẠI" in prompt
    assert "nextQuestion là câu chưa hoàn chỉnh" in prompt


def test_de_bai_khong_co_nhan_xet_thi_khong_them_khoi_nao():
    prompt = build_decide_next_prompt(
        job_category="BE", current_question="Q", transcript="t", history=[],
        asked_count=1, follow_up_count=0, max_questions=10, max_follow_ups=3,
        criteria=_CRITERIA)

    assert "BỊ TRẢ LẠI" not in prompt


# ── Chẩn đoán: số liệu để chốt nguyên nhân ở lớp 3 ─────────────────────────
def test_chan_doan_khong_bao_gio_nem():
    """Test double dựng response bằng `type("R", (), {...})()` nên KHÔNG có `candidates`; đọc
    thẳng thuộc tính sẽ đỏ vì lý do chẳng liên quan tới điều đang kiểm (idiom `extract_usage`).

    Ca `_No` mới là ca đắt: nó thực sự CHẠY vào nhánh except. Không có nó thì `getattr(..., None)`
    che hết và cả hàm "không bao giờ nem" chỉ được khẳng định bằng lời — gỡ try/except đi vẫn xanh.
    """
    tho = type("R", (), {"text": "{}", "usage_metadata": None})()
    assert isinstance(_generation_diagnostics(tho), str)
    assert isinstance(_generation_diagnostics(object()), str)
    assert isinstance(_generation_diagnostics(None), str)

    class _No:
        text = "{}"

        @property
        def candidates(self):
            raise RuntimeError("SDK đổi shape")

    # Một dòng log phụ KHÔNG được đánh hỏng lượt sinh đang chạy.
    assert isinstance(_generation_diagnostics(_No()), str)


def test_chan_doan_neu_duoc_finish_reason_va_so_token():
    """Hai con số này PHÂN BIỆT hai nguyên nhân cần hai cách sửa khác hẳn nhau: `MAX_TOKENS` =
    bị cắt lúc truyền · `STOP` + ít token = chính mô hình tự đóng chuỗi. Không có chúng thì lớp 3
    chỉ đoán."""
    meta = type("M", (), {"candidates_token_count": 17})()
    cand = type("C", (), {"finish_reason": "MAX_TOKENS"})()
    resp = type("R", (), {"text": "{}", "candidates": [cand], "usage_metadata": meta})()

    out = _generation_diagnostics(resp)

    assert "MAX_TOKENS" in out
    assert "17" in out
