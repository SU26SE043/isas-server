# tests/test_score_thinking_budget.py — trần suy luận ẩn cho `score()` (2026-08-20).
#
# Vì sao có vòng này: chấm MỘT câu trả lời mất **19,6s (p50)** trên prod. `ai_usage_logs` chỉ
# thẳng chỗ chảy — operation `score` có output p50 **3.570 token**, còn `decide_next` (đã đặt
# trần 0 từ 2026-08-05) chỉ **126**. `output_tokens = candidates + thoughts` (app/usage.py) nên
# con số đó ĐÃ gộp token suy luận; phần nhìn thấy được trong DB chỉ ~900–1.000 token ⇒ **~2.500
# token là suy luận ẩn**: không ai đọc, vẫn tính tiền theo giá output, vẫn nằm trong thời gian
# ứng viên ngồi chờ.
#
# Vì sao phải có TEST cho một dòng config: hỏng ở đây KHÔNG có triệu chứng. Ai đó gỡ nhầm nhánh
# gắn `thinking_config` (hoặc đọc nhầm `-1` thành "tắt") thì service vẫn chấm đúng, vẫn xanh mọi
# test khác — chỉ là mỗi lượt lại âm thầm đốt lại ngần ấy token và ngần ấy giây. Mẫu test: xem
# test_decide_next.py §thinking + test_cv_analysis_performance.py. KHÔNG gọi Gemini thật.
import json
from types import SimpleNamespace

import pytest

from app.config import Settings, settings
from app.providers.gemini import GeminiProvider

# Rubric tối thiểu đủ để score() chạy trọn (1 tiêu chí, 3 mức hợp lệ) — bài test này soi CONFIG
# đi xuống SDK, không soi logic neo mức (đã có test_scoring.py).
_CRIT = {
    "criterionId": "c1",
    "name": "Độ rõ ràng",
    "description": "Trình bày rõ ràng",
    "maxScore": 5,
    "weight": 1.0,
    "levels": [
        {"score": 0, "descriptor": "Không trả lời được"},
        {"score": 3, "descriptor": "Trả lời được nhưng thiếu chiều sâu"},
        {"score": 5, "descriptor": "Trả lời đầy đủ, có ví dụ"},
    ],
}

_OK_PAYLOAD = {
    "scores": [{"criterionId": "c1", "score": 3, "levelMatched": 3, "reasoning": "có dẫn chứng"}],
    "sampleAnswer": "Câu trả lời mẫu mức 5.",
}


def _capture_config(monkeypatch, provider):
    """Thay chokepoint `_generate` bằng bản ghi lại `config` rồi trả output HỢP LỆ.

    Chặn ở `_generate` chứ không ở `generate_content`: thứ cần khoá là cái config `score()` DỰNG
    ra, tách khỏi mọi chuyện xảy ra bên trong chokepoint (retry/đo token)."""
    captured: dict = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["operation"] = operation
        captured["config"] = config
        return SimpleNamespace(text=json.dumps(_OK_PAYLOAD), usage_metadata=None)

    monkeypatch.setattr(provider, "_generate", fake_generate)
    return captured


def test_mac_dinh_512_khong_phai_0():
    """Khoá con số mặc định + lý do nó KHÁC decide_next (0).

    Chấm là cân nhắc nhiều tiêu chí, mỗi mức phải kèm dẫn chứng lấy từ transcript (E11) — khác
    hẳn decide_next (chọn 1 trong 4 nhánh rồi viết một câu ngắn). Hạ về 0 ở đây là đánh đổi vào
    ĐỘ ĐÚNG CỦA ĐIỂM, phải là một quyết định có người ký chứ không phải một lần sửa tiện tay.
    """
    assert settings.score_thinking_budget == 512
    assert Settings(score_thinking_budget=0).score_thinking_budget == 0   # chỉnh được qua env


@pytest.mark.parametrize("budget", [0, 256, 512])
@pytest.mark.asyncio
async def test_gan_thinking_config_khi_budget_khong_am(monkeypatch, budget):
    """`>= 0` ⇒ PHẢI truyền `thinking_config` xuống SDK với đúng con số đã cấu hình."""
    monkeypatch.setattr(settings, "score_thinking_budget", budget)
    provider = GeminiProvider()
    captured = _capture_config(monkeypatch, provider)

    await provider.score("Q?", "câu trả lời", "BE", [_CRIT])

    assert captured["operation"] == "score"
    tc = getattr(captured["config"], "thinking_config", None)
    assert tc is not None, f"budget={budget} phải truyền thinking_config xuống SDK"
    assert tc.thinking_budget == budget


@pytest.mark.asyncio
async def test_minus_one_tra_lai_mac_dinh_dong_cua_model(monkeypatch):
    """`-1` = cần gạt quay lui: KHÔNG gắn `thinking_config`, để model tự quyết như trước.

    Gắn `ThinkingConfig(thinking_budget=-1)` cũng "trông giống rollback" nhưng là gửi một giá trị
    do ta bịa xuống SDK; ý định ở đây là KHÔNG ĐỤNG VÀO, và chỉ vắng mặt field mới diễn tả đúng
    điều đó (cùng giao ước với `decide_next`/`analyze_cv`).
    """
    monkeypatch.setattr(settings, "score_thinking_budget", -1)
    provider = GeminiProvider()
    captured = _capture_config(monkeypatch, provider)

    await provider.score("Q?", "câu trả lời", "BE", [_CRIT])

    assert getattr(captured["config"], "thinking_config", None) is None


@pytest.mark.asyncio
async def test_phan_con_lai_cua_config_khong_doi(monkeypatch):
    """Trần thinking KHÔNG được kéo theo thay đổi nào khác trong config lượt chấm.

    `temperature` đặc biệt dễ mất khi dựng lại config: E10 (self-consistency) đo ĐỘ PHÂN TÁN giữa
    các lượt chấm — ghim cứng nhiệt độ là mọi lượt ra y hệt nhau, spread luôn 0, cờ needs_review
    không bao giờ bật. Hỏng đó im lặng tuyệt đối vì điểm vẫn hợp lệ.
    """
    monkeypatch.setattr(settings, "score_thinking_budget", 512)
    provider = GeminiProvider()
    captured = _capture_config(monkeypatch, provider)

    await provider.score("Q?", "câu trả lời", "BE", [_CRIT], temperature=0.7)

    config = captured["config"]
    assert config.temperature == 0.7                      # E10 đi xuyên qua, không bị ghim
    assert config.response_mime_type == "application/json"
    assert config.response_schema["required"] == ["scores", "sampleAnswer"]
    assert set(config.response_schema["properties"]) == {"scores", "sampleAnswer"}
