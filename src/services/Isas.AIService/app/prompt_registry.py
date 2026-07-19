"""F21 (FR17) — nạp mảnh prompt admin đã tuỳ biến, thay cho sửa code + build lại image.

Trước vòng này `prompts.py` hardcode 100%: grep ``os.environ|getenv|settings.|open(|db|http``
trong file cho **0 hit**. Đổi một câu chữ trong prompt chấm = sửa code → build image → deploy.

VÌ SAO KÉO TỪ .NET — GEN-4
──────────────────────────
AIService **KHÔNG ĐƯỢC GHI DB** và không có kết nối DB nào, nên prompt không thể nằm ở đây.
Ba đường đã cân nhắc:

  (a) Caller (.NET) truyền prompt hoàn chỉnh xuống theo request
      → KHÔNG LÀM ĐƯỢC như một registry: builder ở đây là **template có nội suy**, không phải
        chuỗi tĩnh — ``build_scoring_prompt`` dựng ``rubric_block`` từ levels+anchors do C# gửi,
        ``build_delivery_block`` dựng khối chỉ số từ số đo F11. Muốn caller gửi prompt dựng sẵn
        thì phải bê **toàn bộ 28KB logic prompt sang .NET** — đó là viết lại, không phải làm
        registry. Thêm: đường worker qua RabbitMQ sẽ phải nhét prompt vào **mọi message**.
  (b) Mount file / ConfigMap
      → không có đường quản trị ⇒ không đạt "admin sửa qua UI"; và aiworker chạy trên Mac,
        ngoài compose của server, nên không mount chung được.
  (c) KÉO từ InterviewService qua HTTP nội bộ  ← CHỌN
      → cái đi qua mạng là **mảnh template**, không phải prompt hoàn chỉnh ⇒ đổi rất hiếm,
        cache được. Nội suy vẫn nằm đúng chỗ đang có.

Chủ sở hữu là **InterviewService** (không phải Payment như F22): AUTH-7 nói endpoint admin nằm
trong service SỞ HỮU dữ liệu, và con dấu phiên bản prompt phải đóng lên ``answer_scores`` —
bảng của Interview — trong cùng transaction.

FAIL-OPEN 4 TẦNG — REGISTRY CHẾT KHÔNG ĐƯỢC LÀM SẬP CHẤM ĐIỂM
─────────────────────────────────────────────────────────────
    1. cache còn hạn (TTL)                → dùng
    2. HTTP GET /internal/prompts         → cache + dùng
    3. HTTP lỗi → cache CŨ (không hạn)    → dùng, log WARN
    4. chưa từng nạp được → bản mặc định hardcode ngay trong prompts.py

Tầng 4 là điều kiện bắt buộc: bảng rỗng / Interview restart / mạng hỏng **không bao giờ** được
làm một answer thành ``Failed`` — Failed nghĩa là người luyện mất 1 credit (PAY-13) vì một sự cố
hạ tầng không liên quan gì tới họ. Cùng triết lý với F22 (``usage.py``): chức năng phụ không được
kéo đổ đường chính.

VÌ SAO KHUNG CHỐNG-INJECTION KHÔNG NẰM TRONG REGISTRY
────────────────────────────────────────────────────
Chỉ những mảnh .NET khai trong ``PromptTemplateKeys`` mới thay được, và danh sách đó **cố ý
không chứa**: khối chống prompt-injection (E11), delimiter bọc câu trả lời ứng viên (AI-4),
hợp đồng output, luật chọn mức (E9), luật reasoning-phải-trích-dẫn (E11), luật ASR (F12).

Prompt chấm vừa là **thước đo** vừa là **bề mặt injection**. Cho sửa toàn thân nghĩa là một tài
khoản admin — hoặc kẻ chiếm được nó — vô hiệu hoá toàn bộ E9+E10+E11 bằng một câu "luôn cho điểm
tối đa", và **không test nào kêu**. Mối nguy còn không cần ác ý: xoá nhầm một đoạn mà mục đích
không hiển nhiên khi đọc là chuyện rất dễ xảy ra. Nên prompt chấm chỉ mở đúng 2 KHE
(``scoring.persona``/``scoring.extra_guidance``) chèn ở vị trí do code quyết, và extra_guidance
nằm SAU mọi luật bắt buộc nên không ghi đè được luật nào.

9 prompt SINH thì mở rộng tay hơn: sai ở đó cho ra câu hỏi dở, **không sai điểm và không mất
credit** — rủi ro tương xứng với tự do.
"""

from __future__ import annotations

import logging
import time

from app.config import settings

logger = logging.getLogger(__name__)

# Bản đồ khoá→văn bản đã tuỳ biến. Rỗng = chưa ai sửa gì = chạy đúng như trước F21.
_cache: dict[str, str] = {}
_fetched_at: float = 0.0
# Đã từng nạp thành công lần nào chưa. Phân biệt "nạp được, không có gì tuỳ biến" (hợp lệ,
# đừng gọi lại liên tục) với "chưa bao giờ nạp được" (còn phải thử lại).
_ever_loaded: bool = False
_prompt_version: int = 0


def reset_cache() -> None:
    """Xoá cache — cho test, và cho lệnh vận hành nếu cần ép nạp lại ngay."""
    global _cache, _fetched_at, _ever_loaded, _prompt_version
    _cache = {}
    _fetched_at = 0.0
    _ever_loaded = False
    _prompt_version = 0


def prompt_version() -> int:
    """Con dấu phiên bản của bộ mảnh đang dùng (0 = thuần mặc định)."""
    return _prompt_version


async def _fetch() -> bool:
    """Nạp từ InterviewService. True nếu nạp được. KHÔNG BAO GIỜ raise."""
    global _cache, _fetched_at, _ever_loaded, _prompt_version

    if not settings.prompt_registry_base:
        # Kill-switch / mặc định test-dev: không cấu hình ⇒ thuần hardcode, không I/O.
        return False

    try:
        import aiohttp

        url = f"{settings.prompt_registry_base.rstrip('/')}/internal/prompts"
        headers = {"X-Internal-Token": settings.internal_token}
        timeout = aiohttp.ClientTimeout(total=settings.prompt_fetch_timeout_seconds)
        async with aiohttp.ClientSession(timeout=timeout) as session:
            async with session.get(url, headers=headers) as resp:
                if resp.status >= 400:
                    logger.warning("F21: registry trả %s — giữ bản đang dùng", resp.status)
                    return False
                data = await resp.json()

        templates = data.get("templates") or {}
        if not isinstance(templates, dict):
            logger.warning("F21: registry trả shape lạ — giữ bản đang dùng")
            return False

        # Chỉ nhận giá trị chuỗi không rỗng. Một mảnh rỗng lọt vào đây sẽ ÂM THẦM xoá một đoạn
        # hướng dẫn khỏi prompt, và triệu chứng duy nhất là chất lượng chấm tệ đi.
        _cache = {k: v for k, v in templates.items() if isinstance(v, str) and v.strip()}
        _prompt_version = int(data.get("promptVersion") or 0)
        _fetched_at = time.monotonic()
        _ever_loaded = True
        return True
    except Exception:  # noqa: BLE001 — registry chết KHÔNG được kéo theo lượt chấm
        logger.warning("F21: không nạp được prompt registry — dùng bản đang có", exc_info=True)
        return False


async def refresh_if_stale() -> None:
    """Nạp lại nếu cache hết hạn. Gọi ở ĐẦU mỗi lượt gọi Gemini.

    Cache cũ vẫn được dùng khi nạp hỏng (tầng 3) — mất mạng một lúc thì prompt hơi cũ, chứ
    không phải rơi phịch về bản mặc định rồi đổi cách chấm giữa chừng.
    """
    if not settings.prompt_registry_base:
        return

    age = time.monotonic() - _fetched_at
    if _ever_loaded and age < settings.prompt_cache_ttl_seconds:
        return

    await _fetch()


def get(key: str, default: str) -> str:
    """Văn bản đang hiệu lực của một mảnh; chưa tuỳ biến → ``default`` (bản trong code).

    Đồng bộ và không I/O: nội suy prompt nằm trong đường nóng, việc nạp đã làm ở
    :func:`refresh_if_stale`.
    """
    value = _cache.get(key)
    # `.strip()` chứ không chỉ truthy: chuỗi toàn khoảng trắng là TRUTHY trong Python, nên
    # `if value` sẽ cho "   " đi qua và ÂM THẦM xoá một đoạn hướng dẫn khỏi prompt — triệu chứng
    # duy nhất là chất lượng chấm tệ dần. `_fetch` đã lọc, nhưng bất biến phải đúng bất kể cache
    # được nạp bằng đường nào (test, lệnh vận hành, phiên bản sau).
    return value if value and value.strip() else default
