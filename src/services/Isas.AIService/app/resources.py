"""F15 (FR09) — làm sạch danh sách TÀI LIỆU HỌC do LLM sinh.

QUYẾT ĐỊNH VỀ URL DO AI SINH — đọc trước khi sửa file này
─────────────────────────────────────────────────────────
LLM sinh URL là **đoán chuỗi trông giống link**, không phải tra cứu. Link bịa
trông y hệt link thật. Rủi ro thật KHÔNG phải "link 404" (phiền, vô hại) mà là
**tên miền bịa**: một domain không tồn tại có thể đã bị người khác đăng ký, đỗ
quảng cáo, hoặc typosquat — lúc đó ta đang đẩy người học tới đó dưới danh nghĩa
"tài liệu hệ thống gợi ý".

Ba phương án đã cân nhắc:
  (a) KHÔNG link, chỉ tên tài liệu     → an toàn tuyệt đối nhưng người học phải
                                          tự mò, giá trị FR09 giảm mạnh.
  (b) Link tự do + ghi chú "chưa kiểm" → ghi chú KHÔNG ngăn được cú click, và
                                          không chặn được domain bịa/thù địch.
  (c) ALLOWLIST TÊN MIỀN (chọn)        → LLM chỉ được trỏ tới các nguồn tài liệu
                                          có thẩm quyền đã biết; host lạ → BỎ URL
                                          nhưng GIỮ tên tài liệu (degrade về (a)
                                          cho đúng mục đó, không mất cả mục).

Điều allowlist BẢO ĐẢM: link trỏ đúng **tên miền** có thật, có thẩm quyền.
Điều allowlist KHÔNG bảo đảm: **đường dẫn** trong tên miền đó có tồn tại — LLM
vẫn có thể bịa path và ra 404. Ta không fetch để xác minh (thêm I/O + phụ thuộc
mạng vào đường sinh lý thuyết đang chạy đồng bộ trong request của người dùng).
Vì vậy FE PHẢI gắn nhãn "liên kết do AI gợi ý, chưa kiểm chứng" — đó là phần bù
cho giới hạn này, không phải câu chống chế.

Muốn bỏ hẳn link: đặt allowlist rỗng → mọi resource về dạng chỉ-tên, không cần
đụng chỗ nào khác.
"""

from urllib.parse import urlparse

# Tên miền tài liệu/khoá học có thẩm quyền. Cố ý NGẮN và bảo thủ: thà bỏ link
# của một nguồn tốt còn hơn để lọt một domain bịa. Thêm domain = quyết định có
# chủ đích, không phải "AI hay nhắc tới nên thêm vào".
ALLOWED_HOSTS: frozenset[str] = frozenset({
    # Tài liệu chính chủ (first-party docs)
    "developer.mozilla.org",
    "learn.microsoft.com",
    "docs.microsoft.com",
    "react.dev",
    "angular.dev",
    "vuejs.org",
    "nodejs.org",
    "docs.python.org",
    "docs.oracle.com",
    "go.dev",
    "kubernetes.io",
    "docs.docker.com",
    "postgresql.org",
    "www.postgresql.org",
    "dev.mysql.com",
    "redis.io",
    "www.rabbitmq.com",
    "spring.io",
    "docs.spring.io",
    "web.dev",
    "www.w3.org",
    "developer.chrome.com",
    # Khoá học / nền tảng học có thật
    "www.coursera.org",
    "www.edx.org",
    "www.freecodecamp.org",
    "roadmap.sh",
    "refactoring.guru",
    # Cộng đồng / chuẩn nghiệp vụ (BA)
    "www.iiba.org",
    "www.scrum.org",
    "www.atlassian.com",
})

# Loại tài liệu cho phép — giữ đóng để FE render icon/nhãn ổn định.
ALLOWED_TYPES: frozenset[str] = frozenset({"Doc", "Course", "Book", "Video", "Article"})

MAX_RESOURCES = 5


def _clean_url(raw) -> str | None:
    """Giữ URL CHỈ KHI https + host nằm trong allowlist. Ngược lại → None."""
    if not isinstance(raw, str) or not raw.strip():
        return None
    try:
        parsed = urlparse(raw.strip())
    except ValueError:
        return None

    # Bắt buộc https: http trần cho phép chèn/nghe lén; scheme khác
    # (javascript:, data:, file:) là bề mặt tấn công, không phải "tài liệu".
    if parsed.scheme != "https":
        return None

    host = (parsed.hostname or "").lower()
    if host not in ALLOWED_HOSTS:
        return None

    return raw.strip()


def count_rejected_urls(raw_items) -> dict | None:
    """F22 — đếm URL do AI ĐỀ XUẤT vs số bị allowlist LOẠI.

    Allowlist hiện loại URL trong IM LẶNG: nếu Gemini bịa tên miền 90% số lần thì
    KHÔNG AI BIẾT, và cũng không có cách nào biết allowlist 30 domain đang quá
    chặt hay quá lỏng. Con số này là thứ duy nhất trả lời được câu đó.

    Đếm trên danh sách THÔ, KHÔNG so với output đã lọc: output còn bị dedup theo
    title và cắt trần ``MAX_RESOURCES``, nên "số url biến mất" ở đó lẫn cả nguyên
    nhân không liên quan tới allowlist. Ở đây chỉ hỏi đúng một câu — trong những
    URL AI đưa ra, bao nhiêu cái trượt allowlist.

    Trả None khi AI không đề xuất URL nào (không có gì để nói về tỉ lệ; ghi 0/0 sẽ
    kéo tỉ lệ trung bình về 0 một cách sai lệch).
    """
    if not isinstance(raw_items, list):
        return None

    proposed = 0
    rejected = 0
    for item in raw_items:
        if not isinstance(item, dict):
            continue
        raw_url = item.get("url")
        if not isinstance(raw_url, str) or not raw_url.strip():
            continue
        proposed += 1
        if _clean_url(raw_url) is None:
            rejected += 1

    if proposed == 0:
        return None
    return {"resourceUrlsProposed": proposed, "resourceUrlsRejected": rejected}


def sanitize_resources(raw_items) -> list[dict]:
    """Chuẩn hoá + lọc list resource thô từ LLM.

    - Bỏ mục không có ``title``.
    - ``type`` lạ → "Doc" (mặc định an toàn, FE luôn render được).
    - ``url`` không qua allowlist → **bỏ cả mục**, để FE không nhận resource chết.
    - Cắt trần ``MAX_RESOURCES`` để 1 bài học không đổ ra danh sách dài vô tận.
    """
    if not isinstance(raw_items, list):
        return []

    out: list[dict] = []
    seen: set[str] = set()

    for item in raw_items:
        if not isinstance(item, dict):
            continue

        title = str(item.get("title") or "").strip()
        if not title:
            continue

        key = title.lower()
        if key in seen:          # LLM hay lặp lại cùng một cuốn/khoá
            continue
        seen.add(key)

        rtype = str(item.get("type") or "").strip()
        if rtype not in ALLOWED_TYPES:
            rtype = "Doc"

        publisher = str(item.get("publisher") or "").strip() or None

        url = _clean_url(item.get("url"))
        if url is None:
            continue

        out.append({
            "title": title,
            "type": rtype,
            "publisher": publisher,
            "url": url,
        })

        if len(out) >= MAX_RESOURCES:
            break

    return out
