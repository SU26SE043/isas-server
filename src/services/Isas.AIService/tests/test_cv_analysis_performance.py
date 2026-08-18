"""AI-CV1 regression gates: prompt budget and fail-safe correctness.

These tests do not call Gemini. The live benchmark is scripts/benchmark-cv-analysis.py;
here we lock the config passed to the real google-genai SDK and deterministic guards.
"""

import json
from types import SimpleNamespace

import pytest

from app.config import settings
from app.providers.gemini import GeminiProvider


def _response(requirement_ids: list[str], citations: list[str] | None = None):
    return SimpleNamespace(text=json.dumps({
        "summary": "Hồ sơ backend có bằng chứng cụ thể.",
        "strengths": ["Python"],
        "weaknesses": ["Chưa thấy Kubernetes"],
        "suggestions": ["Bổ sung kết quả đo được"],
        "requirementMatches": [
            {
                "requirementId": rid,
                "priority": "MustHave",
                "text": "Python",
                "level": "Strong",
                "evidence": "Python",
            }
            for rid in requirement_ids
        ],
        "cvSections": [],
        "citations": [{"chunkId": cid, "content": cid} for cid in (citations or [])],
    }))


@pytest.mark.asyncio
async def test_analyze_cv_caps_grounding_and_disables_thinking(monkeypatch):
    monkeypatch.setattr(settings, "analyze_cv_max_grounding_chunks", 2)
    monkeypatch.setattr(settings, "analyze_cv_thinking_budget", 0)
    provider = GeminiProvider()
    captured = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["operation"] = operation
        captured["contents"] = contents
        captured["config"] = config
        return _response(["r1"], ["c1", "c3"])

    monkeypatch.setattr(provider, "_generate", fake_generate)
    result = await provider.analyze_cv(
        "Skills: Python", "JD", "BE",
        requirements=[{"requirementId": "r1", "priority": "MustHave", "text": "Python"}],
        grounding=[
            {"chunkId": "c1", "content": "source one"},
            {"chunkId": "c2", "content": "source two"},
            {"chunkId": "c3", "content": "must be capped"},
        ],
    )

    assert captured["operation"] == "analyze_cv"
    assert "source one" in captured["contents"]
    assert "source two" in captured["contents"]
    assert "must be capped" not in captured["contents"]
    assert captured["config"].thinking_config.thinking_budget == 0
    matches_schema = captured["config"].response_schema["properties"]["requirementMatches"]
    assert matches_schema["minItems"] == 1
    assert matches_schema["maxItems"] == 1
    assert set(matches_schema["items"]["properties"]) == {"requirementId", "level", "evidence"}
    # The model cited c3, but c3 was outside the capped allowlist.
    assert [item["chunkId"] for item in result["citations"]] == ["c1"]


@pytest.mark.asyncio
async def test_analyze_cv_minus_one_restores_dynamic_thinking(monkeypatch):
    monkeypatch.setattr(settings, "analyze_cv_thinking_budget", -1)
    provider = GeminiProvider()
    captured = {}

    async def fake_generate(operation, *, contents, config, **kwargs):
        captured["config"] = config
        return _response(["r1"])

    monkeypatch.setattr(provider, "_generate", fake_generate)
    await provider.analyze_cv(
        "Python", "JD", "BE",
        requirements=[{"requirementId": "r1", "priority": "MustHave", "text": "Python"}],
    )

    assert captured["config"].thinking_config is None


@pytest.mark.asyncio
async def test_analyze_cv_missing_match_is_safe_weak_not_502(monkeypatch):
    provider = GeminiProvider()
    calls = 0

    async def fake_generate(operation, *, contents, config, **kwargs):
        nonlocal calls
        calls += 1
        return _response(["r-present"])

    monkeypatch.setattr(provider, "_generate", fake_generate)
    result = await provider.analyze_cv(
        "Python", "JD", "BE",
        requirements=[
            {"requirementId": "r-present", "priority": "MustHave", "text": "Python"},
            {"requirementId": "r-missing", "priority": "NiceToHave", "text": "Kubernetes"},
        ],
    )

    assert result["requirementMatches"][1] == {
        "requirementId": "r-missing",
        "priority": "NiceToHave",
        "text": "Kubernetes",
        "level": "Weak",
        "evidence": "Không thấy bằng chứng",
    }
    assert calls == 2


@pytest.mark.asyncio
async def test_analyze_cv_repairs_only_missing_requirement_once(monkeypatch):
    provider = GeminiProvider()
    calls = 0

    async def fake_generate(operation, *, contents, config, **kwargs):
        nonlocal calls
        calls += 1
        if calls == 1:
            return _response(["r-present"])
        return SimpleNamespace(text=json.dumps({
            "summary": "Đã repair requirement còn thiếu.",
            "strengths": [],
            "weaknesses": [],
            "suggestions": [],
            "requirementMatches": [{
                "requirementId": "r-missing",
                "level": "Strong",
                "evidence": "Kubernetes",
            }],
            "cvSections": [],
        }))

    monkeypatch.setattr(provider, "_generate", fake_generate)
    result = await provider.analyze_cv(
        "Python Kubernetes", "JD", "BE",
        requirements=[
            {"requirementId": "r-present", "priority": "MustHave", "text": "Python"},
            {"requirementId": "r-missing", "priority": "NiceToHave", "text": "Kubernetes"},
        ],
    )

    assert calls == 2
    assert result["requirementMatches"][1]["level"] == "Strong"
    assert result["requirementMatches"][1]["evidence"] == "Kubernetes"
