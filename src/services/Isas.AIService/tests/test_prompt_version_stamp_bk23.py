# tests/test_prompt_version_stamp_bk23.py — BK23: đóng dấu prompt_version lên điểm.
#
# F21 đã có cột `answer_scores.prompt_version` + registry biết `prompt_version()`, nhưng
# KHÔNG ai gửi con số đó về .NET ⇒ cột NULL trên mọi dòng. Tính năng "admin sửa prompt chấm"
# chạy mà không ghi lại prompt nào tạo ra điểm nào — sau một lần sửa, điểm cũ và điểm mới
# không còn so sánh được, và không có gì trong dữ liệu nói ra điều đó.
#
# NGUỒN CON DẤU LÀ Ở ĐÂY (AIService), không phải .NET tự đọc DB lúc lưu: registry cache theo
# TTL và cố ý fail-open về cache CŨ khi Interview lỗi (F21, tầng 3) ⇒ phiên bản ĐANG DÙNG để
# chấm thường xuyên khác phiên bản đang nằm trong DB. Chỉ nơi dựng prompt mới biết sự thật.
#
# Test ở đây bám 3 điều:
#   1. score() chụp con dấu ĐÚNG LÚC dựng prompt (không phải lúc nào khác);
#   2. con dấu đi được tới .NET qua callback;
#   3. con dấu KHÔNG BAO GIỜ là đường làm hỏng lượt chấm (registry chết → vẫn chấm).
import json
import pathlib
from unittest.mock import AsyncMock, MagicMock

import pytest

from app import prompt_registry, worker
from app.providers.gemini import GeminiProvider, ScoreOutcome
from app.transcriber import TranscriptionResult
from app.worker import make_score_payload


_CRIT = {
    "criterionId": "c1",
    "name": "Độ rõ ràng",
    "maxScore": 5,
    "levels": [{"score": 0, "descriptor": "kém"}, {"score": 5, "descriptor": "tốt"}],
}

_SCORES = [{"criterionId": "c1", "score": 5, "levelMatched": 5, "reasoning": "rõ."}]


@pytest.fixture(autouse=True)
def _clean_registry():
    """Cache registry là state TOÀN CỤC — không dọn thì con dấu rò từ test này sang test kia."""
    prompt_registry.reset_cache()
    yield
    prompt_registry.reset_cache()


def _resp(payload: dict):
    r = AsyncMock()
    r.text = json.dumps(payload)
    return r


def _provider(payload=None):
    p = GeminiProvider()
    p._client.aio.models.generate_content = AsyncMock(
        return_value=_resp(payload or {"scores": _SCORES}))
    return p


# ── 1. score() chụp con dấu của CHÍNH bộ mảnh vừa dựng prompt ──────────────────────────

@pytest.mark.asyncio
async def test_score_tra_ve_con_dau_dang_hieu_luc():
    prompt_registry._prompt_version = 17
    prompt_registry._ever_loaded = True

    outcome = await _provider().score("Q", "trả lời", "BE", [_CRIT])

    assert outcome.prompt_version == 17


@pytest.mark.asyncio
async def test_registry_thuan_mac_dinh_thi_con_dau_la_0_khong_phai_None():
    """0 = "chấm bằng bản mặc định thuần" — là THÔNG TIN. .NET phân biệt nó với NULL =
    "không biết chấm bằng prompt nào" (worker cũ). Gộp hai ca là mất đúng thứ cần biết."""
    outcome = await _provider().score("Q", "trả lời", "BE", [_CRIT])

    assert outcome.prompt_version == 0


@pytest.mark.asyncio
async def test_con_dau_chup_TRUOC_khi_cache_doi_giua_chung():
    """Con dấu phải thuộc về lượt chấm NÀY.

    Cache là biến module toàn cục, và một lượt refresh khác (task khác trong cùng process,
    hoặc AI3 retry gọi lại score()) có thể đổi nó ngay sau khi prompt đã dựng xong. Nếu con
    dấu được đọc muộn — ví dụ ở worker sau khi score() trả về — nó sẽ khai phiên bản mà lượt
    chấm này CHƯA TỪNG dùng. Một con dấu nói dối còn tệ hơn không có con dấu: cả lý do tồn
    tại của cột là trả lời "hai điểm này có cùng thước đo không".
    """
    prompt_registry._prompt_version = 3
    prompt_registry._ever_loaded = True

    provider = GeminiProvider()

    async def _late_change(*args, **kwargs):
        # Ai đó đổi prompt SAU khi prompt của lượt này đã được dựng và gửi đi.
        prompt_registry._prompt_version = 99
        return _resp({"scores": _SCORES})

    provider._client.aio.models.generate_content = AsyncMock(side_effect=_late_change)

    outcome = await provider.score("Q", "trả lời", "BE", [_CRIT])

    assert outcome.prompt_version == 3, "con dấu phải là bản đã dựng prompt, không phải bản mới nhất"


def test_score_outcome_2_truong_van_dung_duoc_mac_dinh_0():
    """Call site cũ dựng ScoreOutcome 2 trường (test/worker cũ) không được vỡ."""
    assert ScoreOutcome(scores=_SCORES, sample_answer=None).prompt_version == 0


# ── 2. Con dấu đi tới .NET ─────────────────────────────────────────────────────────────

def test_payload_mang_con_dau():
    payload = make_score_payload("a1", "tr", 7, _SCORES, 1, prompt_version=12)
    assert payload["promptVersion"] == 12


def test_payload_khong_truyen_thi_None():
    """Call site positional cũ không phải sửa; .NET nhận None → để cột NULL."""
    payload = make_score_payload("a1", "tr", 7, _SCORES, 1)
    assert payload["promptVersion"] is None


@pytest.mark.asyncio
async def test_worker_chuyen_con_dau_ve_dotnet(monkeypatch):
    """End-to-end trong worker: con dấu từ score() phải nằm trong body gửi .NET.

    Đây là mắt xích đã ĐỨT của F21 — mọi thứ khác đã có sẵn, chỉ thiếu đúng đoạn dây này.
    """
    monkeypatch.setattr(worker.s3_client, "download_fileobj", MagicMock())
    monkeypatch.setattr(worker.transcriber, "transcribe_detailed",
                        MagicMock(return_value=TranscriptionResult(text="tr")))
    monkeypatch.setattr(worker.provider, "score", AsyncMock(
        return_value=ScoreOutcome(scores=_SCORES, sample_answer=None, prompt_version=23)))
    post_callback = AsyncMock()
    monkeypatch.setattr(worker, "post_callback", post_callback)

    message = AsyncMock()
    message.body = json.dumps({
        "answerId": "answer-bk23",
        "audioObjectKey": "recordings/a.webm",
        "questionContent": "Q?",
        "jobCategory": "BE",
        "criteria": [],
        "rubricVersion": 1,
    }).encode()

    await worker.process_message(message)

    assert post_callback.await_args.args[0]["promptVersion"] == 23
    message.ack.assert_awaited_once()


# ── 3. Con dấu KHÔNG được là đường làm hỏng lượt chấm (PAY-13) ─────────────────────────

@pytest.mark.asyncio
async def test_registry_chet_van_cham_duoc_con_dau_ve_0(monkeypatch):
    """Registry không nạp được (Interview down/mạng hỏng) → fail-open, vẫn chấm bằng bản mặc
    định, con dấu 0. Để sự cố hạ tầng làm answer Failed = người luyện mất 1 credit vì chuyện
    không liên quan gì tới họ (PAY-13). Cùng triết lý F21 tầng 4 / F13 / F11."""
    async def _no(*a, **k):
        raise RuntimeError("registry down")

    monkeypatch.setattr(prompt_registry, "_fetch", _no)

    outcome = await _provider().score("Q", "trả lời", "BE", [_CRIT])

    assert outcome.scores[0]["criterionId"] == "c1"   # điểm vẫn về nguyên hợp đồng E9
    assert outcome.prompt_version == 0


# ── 4. Khoá hợp đồng KHOÁ JSON giữa Python và .NET ─────────────────────────────────────

def test_khoa_json_khop_ten_field_ben_dotnet():
    """Python gửi ``promptVersion``; .NET bind ``PromptVersion`` (không phân biệt hoa/thường).

    Đây là mắt xích IM LẶNG nhất của cả đường đi: đổi khoá Python thành ``prompt_version``
    thì .NET bind KHÔNG RA GÌ → cột NULL vĩnh viễn = đúng y nguyên con bug BK23 sinh ra để
    sửa, mà **mọi test hai bên vẫn xanh** (bên Python assert khoá của chính nó, bên .NET
    assert DTO của chính nó — không ai đối chiếu). Nên khoá bằng cách đọc thẳng file .NET,
    theo đúng tiền lệ F21 đã làm với ``PromptTemplateKeys.cs``.
    """
    dto = (pathlib.Path(__file__).resolve().parents[3]
           / "services/Isas.InterviewService/DTOs/ScoringJob.cs").read_text(encoding="utf-8")

    json_key = make_score_payload("a1", "tr", 1, [], 1, prompt_version=1)
    assert "promptVersion" in json_key, "khoá JSON đổi tên — .NET sẽ bind hụt"

    # .NET phải có đúng property tương ứng (PascalCase của khoá JSON) trên DTO callback.
    assert "public int? PromptVersion { get; set; }" in dto, (
        "DTO .NET không còn field PromptVersion khớp khoá promptVersion — "
        "callback sẽ rơi vào hư không và cột lại NULL")
