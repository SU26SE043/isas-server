"""F22 (FR18) — ĐO TOKEN + CHI PHÍ mỗi lần gọi Gemini.

Vấn đề: trước vòng này `providers/gemini.py` gọi ``generate_content`` 10 lần và
**mọi chỗ chỉ đọc ``response.text``** — không chỗ nào chạm ``usage_metadata`` ⇒
hệ thống KHÔNG BIẾT mình đốt bao nhiêu token/tiền. Không có con số đó thì mọi
quyết định về chi phí AI (bật ``SelfConsistencyN``? thêm tiêu chí? sinh câu trả
lời mẫu?) đều là đoán.

VÌ SAO KHÔNG LƯU Ở ĐÂY — GEN-4
──────────────────────────────
AIService **KHÔNG ĐƯỢC GHI DB** (ràng buộc cứng, không phải khuyến nghị). Nên
chỗ lưu số liệu không thể nằm trong service này. Bốn phương án đã cân nhắc:

  (a) Trả usage kèm response cho caller (Interview/Campaign) tự lưu
      → phải đổi 8 response schema + 2 service .NET, và đường worker (chấm qua
        RabbitMQ) KHÔNG có "response" nào để đính kèm — nó gửi callback chấm,
        tức phải nhét usage vào hợp đồng callback của Interview. Số liệu rốt
        cuộc nằm rải ở 2 DB ⇒ không có một chỗ nào trả lời được "tháng này tốn
        bao nhiêu".
  (b) Endpoint /metrics in-memory cho ai đó scrape
      → mất sạch khi restart (aiworker trên Mac restart là chuyện thường), và
        không có Prometheus trong hệ thống để scrape.
  (c) Log có cấu trúc rồi gom ngoài
      → không có hạ tầng log tập trung; log worker hiện còn bị buffer
        (``PYTHONUNBUFFERED`` mới thêm gần đây). Không cho admin xem được gì.
  (d) ĐẨY qua callback nội bộ tới ĐÚNG MỘT service sở hữu bảng  ← CHỌN
      → đây chính là cơ chế GEN-4 đã thiết kế sẵn cho AIService ("trả kết quả
        qua callback, ``X-Internal-Token``"). AIService vẫn stateless/không DB;
        một bảng, một endpoint admin, một chỗ để hỏi.

Service nhận = **PaymentService** (xem `docs/services/payment.md` §F22). Lý do:
chi phí AI là câu hỏi TIỀN, và nó chỉ có nghĩa khi đặt cạnh doanh thu (F19 đã
nằm ở Payment) — "tháng này thu bao nhiêu, đốt bao nhiêu" phải đọc được ở cùng
một chỗ. Đơn giá token cũng do Payment giữ, cùng chỗ với ``product_packages.
price_vnd``, và được SNAPSHOT lên từng dòng (mẫu ``Invoice.UnitPrice``) để số
liệu lịch sử không sai khi Google đổi giá.

BEST-EFFORT — KHÔNG BAO GIỜ LÀM HỎNG ĐƯỜNG CHÍNH
────────────────────────────────────────────────
Đo là chức năng QUAN SÁT. Nếu lỗi đo làm answer `Failed` thì ta vừa biến một
tính năng phụ thành đường mất credit (PAY-13). Vì vậy mọi thứ trong module này
nuốt lỗi và chỉ log: sink chết / mạng hỏng / ``usage_metadata`` đổi shape đều
KHÔNG được nổi lên trên. Đây là ngoại lệ có chủ đích với "đừng nuốt exception".
"""

from __future__ import annotations

import logging
from dataclasses import dataclass

from app.config import settings

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class TokenUsage:
    """Số token 1 lượt gọi.

    ``output_tokens`` = candidates + thoughts (token suy luận của Gemini 2.5).
    Google TÍNH TIỀN token suy luận theo ĐƠN GIÁ OUTPUT, nên phải gộp chúng vào
    output — nếu chỉ lấy ``candidates_token_count`` thì chi phí (Payment tính
    ``prompt*giá_in + output*giá_out``) báo THIẾU (R3: đo thực bỏ sót ~50%).
    ``total`` do SDK trả (authoritative — có thể gồm cả token cached/nội bộ ngoài
    hai vế); KHÔNG tự cộng in+out làm chuẩn."""
    prompt_tokens: int
    output_tokens: int
    total_tokens: int


def _as_int(value) -> int:
    """Ép về int, mọi thứ không ép được → 0.

    Cố ý rộng tay: trong test ``generate_content`` bị mock bằng ``AsyncMock`` nên
    ``usage_metadata`` là Mock và ``int(Mock)`` ném ``TypeError``. Đo không được
    làm vỡ test cũng như đường chạy thật.
    """
    if value is None:
        return 0
    try:
        return max(0, int(value))
    except (TypeError, ValueError):
        return 0


def extract_usage(response) -> TokenUsage | None:
    """Đọc ``response.usage_metadata`` → TokenUsage. KHÔNG BAO GIỜ raise.

    Trả None khi SDK không kèm usage (hoặc shape lạ) — caller ghi log cảnh báo
    chứ không coi là lỗi: mất một dòng thống kê không đáng để hỏng một lượt chấm.
    """
    try:
        meta = getattr(response, "usage_metadata", None)
        if meta is None:
            return None

        prompt = _as_int(getattr(meta, "prompt_token_count", None))
        # candidates = phần trả lời hiển thị; thoughts = token suy luận (Gemini 2.5,
        # KHÔNG nằm trong candidates). Google tính TIỀN token suy luận theo giá
        # OUTPUT ⇒ gộp vào output, nếu không chi phí báo thiếu (R3). ``getattr``
        # thiếu → 0 nên model không bật "thinking" vẫn ra đúng như cũ.
        candidates = _as_int(getattr(meta, "candidates_token_count", None))
        thoughts = _as_int(getattr(meta, "thoughts_token_count", None))
        output = candidates + thoughts
        total = _as_int(getattr(meta, "total_token_count", None))

        # Mock/shape lạ cho ra 0 hết → coi như không có số liệu, đừng ghi dòng rỗng
        # làm bẩn thống kê (0 token là điều KHÔNG THỂ với một lượt gọi thật).
        if prompt == 0 and output == 0 and total == 0:
            return None

        return TokenUsage(prompt_tokens=prompt, output_tokens=output,
                          total_tokens=total or (prompt + output))
    except Exception:  # noqa: BLE001 — xem docstring module: đo không được làm hỏng đường chính
        logger.warning("F22: không đọc được usage_metadata", exc_info=True)
        return None


async def report_usage(operation: str, model: str, response,
                       meta: dict | None = None) -> None:
    """Gửi 1 bản ghi tiêu thụ token về sink (Payment). KHÔNG BAO GIỜ raise.

    ``operation`` = tên đường gọi (generate_questions / score / decide_next …) để
    admin xem được tiêu thụ THEO ENDPOINT, không chỉ tổng.

    ``meta`` = số liệu phụ gắn cùng dòng (hiện chỉ lesson-theory dùng: đếm URL tài
    liệu bị allowlist F15 loại — nếu Gemini bịa domain 90% số lần thì hiện KHÔNG
    AI BIẾT).
    """
    if not settings.usage_metering_enabled:
        return

    usage = extract_usage(response)
    if usage is None:
        logger.info("F22: lượt gọi %s không có usage_metadata — bỏ qua ghi nhận", operation)
        return

    payload = {
        "operation": operation,
        "model": model,
        "promptTokens": usage.prompt_tokens,
        "outputTokens": usage.output_tokens,
        "totalTokens": usage.total_tokens,
    }
    if meta:
        payload.update(meta)

    # Không cấu hình sink → vẫn LOG được con số (còn hơn không biết gì), khỏi gọi mạng.
    # Đây cũng là đường chạy trong test: không có sink ⇒ không có I/O.
    if not settings.usage_sink_base:
        logger.info("F22 usage %s", payload)
        return

    try:
        import aiohttp

        url = f"{settings.usage_sink_base.rstrip('/')}/internal/ai-usage"
        headers = {"X-Internal-Token": settings.internal_token}
        timeout = aiohttp.ClientTimeout(total=settings.usage_sink_timeout_seconds)
        async with aiohttp.ClientSession(timeout=timeout) as session:
            async with session.post(url, json=payload, headers=headers) as resp:
                if resp.status >= 400:
                    logger.warning("F22: sink trả %s cho %s", resp.status, operation)
    except Exception:  # noqa: BLE001 — sink chết KHÔNG được kéo theo lượt chấm
        logger.warning("F22: không gửi được usage cho %s", operation, exc_info=True)
