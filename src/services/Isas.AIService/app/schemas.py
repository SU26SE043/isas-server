from pydantic import BaseModel


class GenerateQuestionsRequest(BaseModel):
    jobCategory: str            # BA | BE | FE
    cvText: str | None = None
    jdText: str | None = None


class GenerateQuestionsResponse(BaseModel):
    questions: list[str]


# ── Đề xuất tiêu chí có cấu trúc (Campaign C8) ──────────────────────────────
class SuggestCriteriaRequest(BaseModel):
    jobCategory: str
    jdText: str | None = None
    criteriaText: str | None = None
    count: int = 4


class CriterionItem(BaseModel):
    name: str
    description: str | None = None
    weight: float            # Σ = 1 (đã chuẩn hoá)
    maxScore: int = 5


class SuggestCriteriaResponse(BaseModel):
    criteria: list[CriterionItem]