"""Wire-contract guard for the bilingual language field.

Pydantic ignores unknown fields by default, so a missing declaration would silently make one
endpoint fall back to Vietnamese while every HTTP call still returns 200.
"""

from app.schemas import (
    AnalyzeCvRequest,
    AnalyzeRepoRequest,
    DecideNextRequest,
    GenerateLessonTheoryRequest,
    GenerateQuestionsRequest,
    GenerateRoadmapRequest,
    SuggestCriteriaRequest,
    SummarizeRoadmapRequest,
    SummarizeSessionRequest,
    TtsRequest,
)


LANGUAGE_AWARE_MODELS = (
    GenerateQuestionsRequest,
    SuggestCriteriaRequest,
    AnalyzeCvRequest,
    AnalyzeRepoRequest,
    GenerateRoadmapRequest,
    GenerateLessonTheoryRequest,
    SummarizeRoadmapRequest,
    SummarizeSessionRequest,
    DecideNextRequest,
    TtsRequest,
)


def test_every_language_aware_request_declares_language() -> None:
    for model in LANGUAGE_AWARE_MODELS:
        assert "language" in model.model_fields, model.__name__
