# tests/test_lesson_resources.py — F15 (FR09): tài liệu học gợi ý cho lesson.
#
# Trọng tâm test KHÔNG phải "có trả về resources không" mà là **xử lý URL do LLM
# sinh**: link bịa trông y hệt link thật, và tên miền bịa có thể đã bị người khác
# đăng ký. Hợp đồng ta khoá ở đây: url chỉ sống sót nếu https + host thuộc
# allowlist; ngược lại BỎ URL nhưng GIỮ tên tài liệu.
import json
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.config import settings
from app.providers.gemini import GeminiProvider
from app.resources import sanitize_resources

client = TestClient(main_module.app)

# Q2/GEN-7 — endpoint SINH nay gate X-Internal-Token (fail-closed): mọi call hợp lệ phải
# kèm _HEADERS. Nhánh 401 nằm ở tests/test_internal_token_gate_q2.py.
_HEADERS = {"X-Internal-Token": settings.internal_token}


def _fake_gemini_response(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


# ── sanitize_resources: URL allowlist ───────────────────────────────────────


def test_keeps_url_from_allowlisted_host():
    out = sanitize_resources([
        {"title": "MDN: Event loop", "type": "Doc",
         "url": "https://developer.mozilla.org/en-US/docs/Web/JavaScript/EventLoop"},
    ])
    assert out[0]["url"].startswith("https://developer.mozilla.org/")


def test_unknown_host_drops_url_but_keeps_title():
    """🔑 Host ngoài allowlist: BỎ URL, GIỮ tên (phương án (c) ở docstring module — degrade về "chỉ tên"
    cho đúng mục đó). ⚠ ĐỔI TIỀN ĐỀ 2026-09-15: bản cũ bỏ CẢ MỤC, tự mâu thuẫn với chính prompt
    ("không chắc thì ĐỂ TRỐNG url — tài liệu chỉ có tên vẫn hữu ích") ⇒ bài BA 0 tài nguyên trên prod.
    Mutation: trả lại `if url is None: continue` → ĐỎ."""
    out = sanitize_resources([
        {"title": "Sách hay về backend", "type": "Book",
         "url": "https://totally-real-backend-book.example.com/ch1"},
    ])
    assert out == [{"title": "Sách hay về backend", "type": "Book", "publisher": None, "url": None}]


@pytest.mark.parametrize("bad_url", [
    "http://developer.mozilla.org/docs",       # http trần — kể cả host hợp lệ
    "javascript:alert(1)",
    "data:text/html,<script>alert(1)</script>",
    "file:///etc/passwd",
    "//developer.mozilla.org/docs",            # scheme-relative
    "https://developer.mozilla.org.evil.com/", # typosquat kiểu TIỀN TỐ: host thật nằm đầu, domain lạ ở cuối
    # Typosquat kiểu HẬU TỐ — vector khác hẳn ca trên và từng KHÔNG có test nào bắt (supervisor phát hiện
    # bằng mutation `host.endswith(h)`: mutation đó xanh 143/143 trong khi ca `.evil.com` vẫn đỏ).
    # Đáng khoá vì `endswith` là refactor RẤT dễ xảy ra — người ta hay đổi sang thế để cho phép subdomain
    # (`docs.mozilla.org`), và lúc đó `evilmozilla.org` lọt allowlist mà không test nào kêu.
    "https://evildeveloper.mozilla.org.attacker.net/",
    "https://notdeveloper.mozilla.org/",
    "",
    None,
    12345,
])
def test_rejects_dangerous_or_malformed_urls(bad_url):
    """URL nguy hiểm/lệch/trống: LINK bị bỏ (url=None), mục vẫn còn tên — cái cần chặn là cú click,
    không phải cái tên tài liệu."""
    out = sanitize_resources([{"title": "X", "type": "Doc", "url": bad_url}])
    assert len(out) == 1
    assert out[0]["title"] == "X"
    assert out[0]["url"] is None


def test_unknown_type_falls_back_to_doc():
    out = sanitize_resources([{"title": "X", "type": "Podcast", "url": "https://react.dev/"}])
    assert out[0]["type"] == "Doc"


def test_drops_items_without_title():
    out = sanitize_resources([
        {"type": "Doc", "url": "https://react.dev/"},
        {"title": "   ", "type": "Doc"},
        {"title": "Có tên", "type": "Doc", "url": "https://react.dev/"},
    ])
    assert [r["title"] for r in out] == ["Có tên"]


def test_dedupes_by_title_case_insensitive():
    out = sanitize_resources([
        {"title": "Clean Code", "type": "Book", "url": "https://react.dev/"},
        {"title": "clean code", "type": "Book", "url": "https://react.dev/"},
    ])
    assert len(out) == 1


def test_caps_number_of_resources():
    out = sanitize_resources([
        {"title": f"R{i}", "type": "Doc", "url": "https://react.dev/"} for i in range(20)
    ])
    assert len(out) == 5


def test_non_list_input_is_safe():
    assert sanitize_resources(None) == []
    assert sanitize_resources("not a list") == []
    assert sanitize_resources([None, "x", 3]) == []


# ── Provider: sanitize được áp trên đường đi thật ───────────────────────────


@pytest.mark.asyncio
async def test_provider_sanitizes_resources_from_llm(lesson_theory_payload):
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(lesson_theory_payload(
            ["Thiết kế CSDL"],
            resources=[
                {"title": "PostgreSQL docs: Transactions", "type": "Doc",
                 "publisher": "PostgreSQL", "url": "https://www.postgresql.org/docs/current/tutorial-transactions.html"},
                {"title": "Khoá học bịa", "type": "Course",
                 "url": "https://khoahoc-khong-co-that.example/abc"},
            ],
        ))
    )

    # RAG grounding: generate_lesson_theory() nay trả 4 trường (theory, resources, cited,
    # mistake_review) — mẫu F13, mở rộng MIS1-B1.
    theory, resources, cited, _ = await provider.generate_lesson_theory(
        "BE", "Junior", "Transaction", ["Thiết kế CSDL"], None)

    assert theory.startswith("# Transaction")
    assert len(resources) == 2
    assert resources[0]["url"] is not None          # host allowlist → giữ link
    assert resources[1]["url"] is None              # host bịa → bỏ link, giữ tên
    assert resources[1]["title"] == "Khoá học bịa"
    assert cited is None                            # ungrounded → không citation


@pytest.mark.asyncio
async def test_provider_missing_resources_is_not_an_error(lesson_theory_payload):
    """resources rỗng KHÔNG phải lỗi — bài giảng vẫn dùng được (khác với bài thiếu nội dung)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response(lesson_theory_payload(["Bài"]))
    )

    theory, resources, _, _ = await provider.generate_lesson_theory("BE", "Junior", "Bài", [], None)
    assert theory
    assert resources == []


# ── Prompt: lớp phòng thủ thứ nhất (bảo mô hình đừng đoán link) ─────────────


def test_lesson_theory_prompt_forbids_guessing_urls():
    from app.prompts import build_lesson_theory_prompt

    prompt = build_lesson_theory_prompt("BE", "Junior", "Transaction", ["CSDL"], None)

    assert "KHÔNG ĐƯỢC đoán" in prompt
    assert "ĐỂ TRỐNG url" in prompt
    assert "docs.spring.io" in prompt             # có nêu ví dụ nguồn chính chủ của NGÀNH (BE)


def test_lesson_theory_prompt_vi_du_host_theo_nganh():
    """Bài BA phải thấy host BA, không phải 12 host đầu theo ABC (toàn dev-tool) — đó là lý do URL
    bài BA bị allowlist loại 100% (đo prod 2026-09-14). Mutation: quay lại `sorted(...)[:12]` → ĐỎ."""
    from app.prompts import build_lesson_theory_prompt

    ba = build_lesson_theory_prompt("BA", "Junior", "Ưu tiên backlog", ["Phân tích yêu cầu"], None)
    assert "www.atlassian.com" in ba and "www.agilealliance.org" in ba
    assert "docs.docker.com" not in ba and "angular.dev" not in ba

    fe = build_lesson_theory_prompt("FE", "Junior", "Hooks", ["React"], None)
    assert "react.dev" in fe and "developer.mozilla.org" in fe
    assert "www.agilealliance.org" not in fe


def test_vi_du_host_theo_nganh_deu_nam_trong_allowlist():
    """Ví dụ đưa cho model mà không qua được allowlist thì model làm đúng vẫn bị loại — hai bảng
    phải nhất quán."""
    from app.resources import ALLOWED_HOSTS, RESOURCE_HOST_EXAMPLES, resource_host_examples
    for cat, hosts in RESOURCE_HOST_EXAMPLES.items():
        for h in hosts:
            assert h in ALLOWED_HOSTS, f"{cat}: {h}"
    for h in resource_host_examples("NGÀNH-LẠ"):
        assert h in ALLOWED_HOSTS
    assert resource_host_examples("ba") == RESOURCE_HOST_EXAMPLES["BA"]


def test_host_ba_khop_ca_dang_www_lan_tran():
    """Khớp host CHÍNH XÁC (không suffix) nên cả hai dạng phải được khai — thiếu một là link đúng
    nguồn vẫn bị bỏ."""
    out = sanitize_resources([
        {"title": "A", "type": "Doc", "url": "https://www.agilealliance.org/glossary/invest/"},
        {"title": "B", "type": "Doc", "url": "https://agilealliance.org/glossary/invest/"},
        {"title": "C", "type": "Doc", "url": "https://camunda.com/bpmn/reference/"},
        {"title": "D", "type": "Doc", "url": "https://docs.camunda.io/docs/components/modeler/bpmn/"},
    ])
    assert all(r["url"] is not None for r in out), out


# ── Endpoint ────────────────────────────────────────────────────────────────


def test_endpoint_returns_resources(monkeypatch):
    async def fake(job_category, level, lesson_title, focus_criteria, weaknesses,
                   grounding=None, evidence=None, mode=None, mistakes=None):
        return "# Bài\n\nND", [
            {"title": "MDN", "type": "Doc", "publisher": "Mozilla",
             "url": "https://developer.mozilla.org/"},
        ], None, None

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)

    res = client.post(
        "/api/v1/generate-lesson-theory",
        headers=_HEADERS,
        json={"jobCategory": "BE", "level": "Junior", "lessonTitle": "Bài",
              "focusCriteria": []},
    )

    assert res.status_code == 200
    body = res.json()
    assert body["resources"] == [
        {"title": "MDN", "type": "Doc", "publisher": "Mozilla",
         "url": "https://developer.mozilla.org/"},
    ]
