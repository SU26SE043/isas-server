"""Guards that provider response schemas are accepted by the google-genai SDK."""

from types import SimpleNamespace

import pytest

import app.providers.gemini as gemini_module
from app.providers.gemini import GeminiProvider


@pytest.mark.asyncio
async def test_grounded_cv_jd_schemas_are_valid_google_genai_schemas(monkeypatch):
    """The real SDK must validate every grounded schema construction path."""
    original_config = gemini_module.types.GenerateContentConfig
    seen_operations = []

    def validating_config(**kwargs):
        # Do not replace the SDK validation with a mock: this is the contract under test.
        return original_config(**kwargs)

    monkeypatch.setattr(gemini_module.types, "GenerateContentConfig", validating_config)

    provider = GeminiProvider()

    async def fake_generate(operation, *, contents, config, **kwargs):
        seen_operations.append(operation)
        if operation == "suggest_jd_requirements":
            return SimpleNamespace(text='{"mustHave": [], "niceToHave": []}')
        return SimpleNamespace(text=(
            '{"summary":"s","strengths":[],"weaknesses":[],"suggestions":[],'
            '"requirementMatches":[{"requirementId":"r1","level":"Strong",'
            '"evidence":"Python"}],"cvSections":[],"citations":[]}'
        ))

    monkeypatch.setattr(provider, "_generate", fake_generate)
    grounding = [{"chunkId": "c1", "content": "Python", "sourceUrl": None, "sourceTitle": None}]

    await provider.suggest_jd_requirements("Need Python", "BE", grounding=grounding)
    await provider.analyze_cv(
        "Python", "JD", "BE",
        requirements=[{"requirementId": "r1", "priority": "MustHave", "text": "Python"}],
        grounding=grounding,
    )

    assert seen_operations == ["suggest_jd_requirements", "analyze_cv"]
