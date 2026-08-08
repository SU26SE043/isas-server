import asyncio
import json
from types import SimpleNamespace
import pytest

from app.providers.gemini import GeminiProvider
from app.config import settings

C1 = "11111111-1111-1111-1111-111111111111"
C2 = "22222222-2222-2222-2222-222222222222"

class Models:
    def __init__(self): self.calls = 0; self.prompts = []
    async def generate_content(self, *, model, contents, config):
        self.calls += 1; self.prompts.append(contents)
        payload = ({"questions":[{"text":"Q1?","targetCriterionIds":[C1]}, {"text":"Q2?","targetCriterionIds":[C2]}]}
                   if self.calls == 2 else {"questions":[{"text":"Q1?","targetCriterionIds":[C1]}, {"text":"Q2?","targetCriterionIds":[C1]}]})
        return SimpleNamespace(text=json.dumps(payload))

@pytest.mark.asyncio
async def test_missing_coverage_retries_once_with_named_feedback(monkeypatch):
    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider(); models = Models()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=models))
    monkeypatch.setattr(settings, "question_max_attempts", 2)
    result = await provider.generate("BE", None, None, count=2, criteria=[
        {"criterionId": C1, "name":"Kỹ thuật"}, {"criterionId": C2, "name":"Thiết kế"}])
    assert models.calls == 2
    assert "Thiết kế" in models.prompts[1]
    assert result.target_criteria == [[C1], [C2]]


@pytest.mark.asyncio
async def test_verify_uses_grounding_for_citations_without_injecting_it_into_generation(monkeypatch):
    class VerifyModels:
        def __init__(self): self.calls = []
        async def generate_content(self, *, model, contents, config):
            self.calls.append(contents)
            if len(self.calls) == 1:
                return SimpleNamespace(text=json.dumps({"questions": [{"text": "Q?", "citedChunkIds": []}]}))
            return SimpleNamespace(text=json.dumps({"checks": [{"questionIndex": 0, "citedChunkIds": ["c1"], "reason": None}]}))
    monkeypatch.setattr(GeminiProvider, "__init__", lambda self: None)
    provider = GeminiProvider(); models = VerifyModels()
    provider._client = SimpleNamespace(aio=SimpleNamespace(models=models))
    monkeypatch.setattr(settings, "question_verify_enabled", True)
    result = await provider.generate("BE", None, None, count=1,
                                     grounding=[{"chunkId": "c1", "content": "Nguồn đúng"}])
    assert "TÀI LIỆU THAM CHIẾU UY TÍN" not in models.calls[0]
    assert result.citations == [{"questionIndex": 0, "citedChunkIds": ["c1"]}]
