# tests/test_grounding.py — RAG grounding (Contract 1 + 2).
#
# Hai thứ được khoá ở đây:
#   1. /embed (Contract 1): sinh vector, gate X-Internal-Token (GEN-7), lỗi provider → 502.
#   2. Grounding + citation (Contract 2): tài liệu tham chiếu chèn vào prompt SINH, model trả
#      citedChunkIds, và AIService DROP mọi id KHÔNG thuộc tập đã cấp (chống bịa by-construction).
#
# ⚠ Unit test chỉ chứng minh ĐẤU DÂY (mock Gemini) — KHÔNG chứng minh chunk truy hồi đúng. Chất
# lượng retrieval verify ở L3 + Phase 2 (đo), như plan đã ghi.
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.config import settings
from app.prompts import build_grounding_block, build_prompt, build_lesson_theory_prompt
from app.providers.gemini import GeminiProvider

client = TestClient(main_module.app)

_HEADERS = {"X-Internal-Token": settings.internal_token}

_GROUNDING = [
    {"chunkId": "c1", "content": "useEffect chạy sau mỗi lần render...",
     "sourceUrl": "https://react.dev/reference/react/useEffect", "sourceTitle": "react.dev — useEffect"},
    {"chunkId": "c2", "content": "Dependency array kiểm soát khi nào effect chạy lại...",
     "sourceUrl": "https://react.dev/learn", "sourceTitle": "react.dev — Learn"},
]


def _fake_response(payload: dict):
    return SimpleNamespace(text=json.dumps(payload))


# ══════════════════════════════════════════════════════════════════════════════
# Contract 1 — /embed
# ══════════════════════════════════════════════════════════════════════════════

def test_embed_missing_token_returns_401():
    """GEN-7 fail-closed: thiếu X-Internal-Token → 401 TRƯỚC khi chạm provider."""
    res = client.post("/api/v1/embed",
                      json={"texts": ["a"], "taskType": "RETRIEVAL_QUERY"})
    assert res.status_code == 401


def test_embed_wrong_token_returns_401():
    res = client.post("/api/v1/embed",
                      headers={"X-Internal-Token": "sai-token"},
                      json={"texts": ["a"], "taskType": "RETRIEVAL_QUERY"})
    assert res.status_code == 401


def test_embed_returns_vectors_dim_model(monkeypatch):
    """Số vector = số text; dim + model khớp config (khớp collection Qdrant)."""
    async def fake_embed(texts, task_type):
        assert task_type == "RETRIEVAL_DOCUMENT"
        return [[0.1] * settings.embed_dim for _ in texts]

    monkeypatch.setattr(main_module.provider, "embed", fake_embed)

    res = client.post("/api/v1/embed", headers=_HEADERS,
                      json={"texts": ["đoạn 1", "đoạn 2"], "taskType": "RETRIEVAL_DOCUMENT"})

    assert res.status_code == 200
    body = res.json()
    assert len(body["vectors"]) == 2
    assert all(len(v) == settings.embed_dim for v in body["vectors"])
    assert body["dim"] == settings.embed_dim
    assert body["model"] == settings.embed_model


def test_embed_provider_error_returns_502(monkeypatch):
    """Gemini quá tải / model lạ → 502 (không nuốt lỗi thành 200 rỗng)."""
    async def boom(texts, task_type):
        raise RuntimeError("Gemini 503")

    monkeypatch.setattr(main_module.provider, "embed", boom)

    res = client.post("/api/v1/embed", headers=_HEADERS,
                      json={"texts": ["a"], "taskType": "RETRIEVAL_QUERY"})
    assert res.status_code == 502
    assert "Lỗi sinh embedding" in res.json()["detail"]


@pytest.mark.asyncio
async def test_provider_embed_maps_embeddings_values():
    """provider.embed đọc resp.embeddings[].values đúng thứ tự + đúng config."""
    provider = GeminiProvider()
    captured = {}

    async def fake_embed_content(*, model, contents, config):
        captured["model"] = model
        captured["contents"] = contents
        captured["output_dimensionality"] = config.output_dimensionality
        captured["task_type"] = config.task_type
        return SimpleNamespace(embeddings=[
            SimpleNamespace(values=[0.1, 0.2, 0.3]),
            SimpleNamespace(values=[0.4, 0.5, 0.6]),
        ])

    provider._client.aio.models.embed_content = fake_embed_content

    vectors = await provider.embed(["x", "y"], "RETRIEVAL_QUERY")

    assert vectors == [[0.1, 0.2, 0.3], [0.4, 0.5, 0.6]]
    assert captured["model"] == settings.embed_model
    assert captured["output_dimensionality"] == settings.embed_dim
    assert captured["task_type"] == "RETRIEVAL_QUERY"
    assert captured["contents"] == ["x", "y"]


# ══════════════════════════════════════════════════════════════════════════════
# Contract 2 — build_grounding_block (khối HARDCODE)
# ══════════════════════════════════════════════════════════════════════════════

def test_grounding_block_none_when_empty():
    assert build_grounding_block(None) is None
    assert build_grounding_block([]) is None


def test_grounding_block_cite_true_has_chunkids_and_citation_rule():
    block = build_grounding_block(_GROUNDING, cite=True)
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" in block
    assert "[chunkId=c1]" in block
    assert "[chunkId=c2]" in block
    assert "react.dev — useEffect" in block
    # Lớp phòng thủ THỨ NHẤT: bảo model chỉ cite id đã cấp, không bịa.
    assert "citedChunkIds" in block
    assert "KHÔNG bịa chunkId" in block


def test_grounding_block_cite_false_omits_citation_output():
    """Roadmap (cite=False): ưu tiên nguồn nhưng KHÔNG yêu cầu trả citedChunkIds."""
    block = build_grounding_block(_GROUNDING, cite=False)
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" in block
    assert "[chunkId=c1]" in block
    assert "citedChunkIds" not in block
    assert "KHÔNG bịa nội dung" in block


def test_grounding_block_drops_chunk_without_id():
    block = build_grounding_block([{"content": "không có chunkId"}], cite=True)
    assert block is None


# ══════════════════════════════════════════════════════════════════════════════
# Contract 2 — build_prompt / build_lesson_theory_prompt injection
# ══════════════════════════════════════════════════════════════════════════════

def test_build_prompt_injects_grounding_and_citation_format():
    prompt = build_prompt("FE", None, None, 3, None, _GROUNDING)
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" in prompt
    assert "[chunkId=c1]" in prompt
    # Output shape đổi sang per-question citedChunkIds khi có grounding.
    assert '"citedChunkIds"' in prompt


def test_build_prompt_without_grounding_unchanged():
    prompt = build_prompt("FE", None, None, 3)
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" not in prompt
    assert '{"questions": ["câu 1", "câu 2", ...]}' in prompt


def test_build_lesson_theory_prompt_injects_grounding():
    prompt = build_lesson_theory_prompt("FE", "Junior", "useEffect", ["React"], None, _GROUNDING)
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" in prompt
    assert "[chunkId=c1]" in prompt
    assert '"citedChunkIds"' in prompt


def test_build_lesson_theory_prompt_without_grounding_unchanged():
    prompt = build_lesson_theory_prompt("FE", "Junior", "useEffect", ["React"], None)
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" not in prompt


# ══════════════════════════════════════════════════════════════════════════════
# Contract CITATION — provider.generate: drop id lạ, ungrounded → không citation
# ══════════════════════════════════════════════════════════════════════════════

def _provider_with(payload: dict):
    """GeminiProvider với generate_content trả payload cố định (không chạm mạng)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_response(payload))
    return provider


@pytest.mark.asyncio
async def test_generate_grounded_keeps_only_provided_chunkids():
    """🔑 Chống bịa by-construction: model trả 1 id LẠ ('GHOST') → AIService DROP nó."""
    provider = _provider_with({
        "questions": [
            {"text": "Q1 về useEffect?", "citedChunkIds": ["c1", "GHOST"]},
            {"text": "Q2 về dependency?", "citedChunkIds": ["c2"]},
            {"text": "Q3 tổng quát?", "citedChunkIds": []},
        ]
    })

    result = await provider.generate("FE", None, None, count=3, grounding=_GROUNDING)

    assert result.questions == ["Q1 về useEffect?", "Q2 về dependency?", "Q3 tổng quát?"]
    assert result.citations == [
        {"questionIndex": 0, "citedChunkIds": ["c1"]},   # GHOST bị drop
        {"questionIndex": 1, "citedChunkIds": ["c2"]},
        {"questionIndex": 2, "citedChunkIds": []},
    ]


@pytest.mark.asyncio
async def test_generate_grounded_drops_all_foreign_ids():
    """Model cite TOÀN id không thuộc tập cấp → citedChunkIds rỗng (ungrounded cho câu đó)."""
    provider = _provider_with({
        "questions": [{"text": "Q1?", "citedChunkIds": ["FAKE1", "FAKE2"]}]
    })
    result = await provider.generate("FE", None, None, count=1, grounding=_GROUNDING)
    assert result.citations == [{"questionIndex": 0, "citedChunkIds": []}]


@pytest.mark.asyncio
async def test_generate_ungrounded_no_citations():
    """Không grounding → schema cũ {questions:[str]}, citations = None (shape cũ giữ nguyên)."""
    provider = _provider_with({"questions": ["Q1", "Q2"]})
    result = await provider.generate("FE", None, None, count=2)
    assert result.questions == ["Q1", "Q2"]
    assert result.citations is None


@pytest.mark.asyncio
async def test_generate_grounded_dedupes_cited_ids():
    provider = _provider_with({
        "questions": [{"text": "Q1?", "citedChunkIds": ["c1", "c1", "c2"]}]
    })
    result = await provider.generate("FE", None, None, count=1, grounding=_GROUNDING)
    assert result.citations == [{"questionIndex": 0, "citedChunkIds": ["c1", "c2"]}]


# ══════════════════════════════════════════════════════════════════════════════
# Contract CITATION — provider.generate_lesson_theory
# ══════════════════════════════════════════════════════════════════════════════

@pytest.mark.asyncio
async def test_lesson_theory_grounded_filters_cited_ids():
    provider = _provider_with({
        "sections": [{"criterion": "React", "heading": "useEffect",
                      "body": "Nội dung giải thích useEffect..."}],
        "example": "Ví dụ: cleanup khi unmount.",
        "commonMistakes": "Quên dependency array.",
        "resources": [],
        "citedChunkIds": ["c1", "GHOST", "c2"],
    })
    theory, resources, cited = await provider.generate_lesson_theory(
        "FE", "Junior", "useEffect", ["React"], None, _GROUNDING)

    assert theory.startswith("# useEffect")
    assert cited == ["c1", "c2"]     # GHOST bị drop


@pytest.mark.asyncio
async def test_lesson_theory_ungrounded_cited_is_none():
    provider = _provider_with({
        "sections": [{"criterion": "React", "heading": "Bài", "body": "ND đủ ý."}],
        "example": "Ví dụ.", "commonMistakes": "Lỗi hay gặp.", "resources": [],
    })
    theory, resources, cited = await provider.generate_lesson_theory(
        "FE", "Junior", "useEffect", ["React"], None)
    assert cited is None


# ══════════════════════════════════════════════════════════════════════════════
# Endpoint — /generate-questions + /generate-lesson-theory với grounding
# ══════════════════════════════════════════════════════════════════════════════

def test_endpoint_generate_questions_returns_citations(monkeypatch):
    from app.providers.gemini import QuestionGenerationResult

    # `criteria` (chấm-theo-phạm-vi) là tham số MỚI của provider.generate, endpoint truyền
    # positional ⇒ double phải nới, nếu không TypeError → 502 (mẫu C14 nới double /analyze-cv).
    # Nhận None ở đây cũng chính là bằng chứng: request không có criteria thì không có gì phát sinh.
    async def fake_generate(job_category, cv_text, jd_text, count=None,
                            focus_criteria=None, grounding=None, criteria=None,
                            seniority=None):
        # grounding phải được truyền xuống (không bị pydantic nuốt).
        assert grounding == [{"chunkId": "c1", "content": "x",
                              "sourceUrl": None, "sourceTitle": None}]
        assert criteria is None
        return QuestionGenerationResult(
            questions=["Q1"], citations=[{"questionIndex": 0, "citedChunkIds": ["c1"]}])

    monkeypatch.setattr(main_module.provider, "generate", fake_generate)

    res = client.post("/api/v1/generate-questions", headers=_HEADERS, json={
        "jobCategory": "FE",
        "grounding": [{"chunkId": "c1", "content": "x"}],
    })

    assert res.status_code == 200
    body = res.json()
    assert body["questions"] == ["Q1"]
    assert body["citations"] == [{"questionIndex": 0, "citedChunkIds": ["c1"]}]


def test_endpoint_generate_questions_ungrounded_omits_citations(monkeypatch):
    """Campaign B2B (không grounding): response CHỈ có questions, KHÔNG có field citations."""
    from app.providers.gemini import QuestionGenerationResult

    async def fake_generate(job_category, cv_text, jd_text, count=None,
                            focus_criteria=None, grounding=None, criteria=None,
                            seniority=None):
        assert grounding is None
        assert criteria is None
        return QuestionGenerationResult(questions=["Q1", "Q2"], citations=None)

    monkeypatch.setattr(main_module.provider, "generate", fake_generate)

    res = client.post("/api/v1/generate-questions", headers=_HEADERS, json={"jobCategory": "FE"})

    assert res.status_code == 200
    body = res.json()
    assert body == {"questions": ["Q1", "Q2"]}   # exclude_none → không có key "citations"


def test_endpoint_generate_lesson_theory_returns_cited_chunkids(monkeypatch):
    async def fake(job_category, level, lesson_title, focus_criteria, weaknesses,
                   grounding=None, evidence=None):
        assert grounding == [{"chunkId": "c1", "content": "x",
                              "sourceUrl": None, "sourceTitle": None}]
        return "# useEffect\n\nND", [], ["c1"]

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "FE", "level": "Junior", "lessonTitle": "useEffect",
        "focusCriteria": ["React"],
        "grounding": [{"chunkId": "c1", "content": "x"}],
    })

    assert res.status_code == 200
    body = res.json()
    assert body["theoryMarkdown"].startswith("# useEffect")
    assert body["citedChunkIds"] == ["c1"]


def test_endpoint_generate_lesson_theory_ungrounded_omits_cited(monkeypatch):
    async def fake(job_category, level, lesson_title, focus_criteria, weaknesses,
                   grounding=None, evidence=None):
        assert grounding is None
        return "# Bài\n\nND", [], None

    monkeypatch.setattr(main_module.provider, "generate_lesson_theory", fake)

    res = client.post("/api/v1/generate-lesson-theory", headers=_HEADERS, json={
        "jobCategory": "FE", "level": "Junior", "lessonTitle": "Bài", "focusCriteria": []})

    assert res.status_code == 200
    body = res.json()
    assert "citedChunkIds" not in body    # exclude_none → shape cũ giữ nguyên
    assert body["resources"] == []
