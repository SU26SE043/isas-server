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


# ── Phân tích CV (B2C BC6, D17) — sync HTTP, dùng chung engine với B2B (C14) ─
class AnalyzeCvRequest(BaseModel):
    cvText: str
    jdText: str | None = None
    jobCategory: str | None = None   # BA | BE | FE — optional (chỉ để cá nhân hoá nhận xét)


class JdMatch(BaseModel):
    score: int                # 0-100
    matchedSkills: list[str]
    missingSkills: list[str]


class AnalyzeCvResponse(BaseModel):
    summary: str
    strengths: list[str]
    weaknesses: list[str]
    suggestions: list[str]
    jdMatch: JdMatch | None = None   # chỉ có khi request có jdText


# ── Roadmap ôn tập cá nhân hoá B2C (BC13, D20) — 3 endpoint sync, stateless ─
class WeaknessScore(BaseModel):
    criterionName: str
    percentage: float


class GenerateRoadmapRequest(BaseModel):
    jobCategory: str
    level: str                                     # Fresher | Junior | Middle | Senior
    weaknesses: list[WeaknessScore] | None = None  # từ session_criterion_scores; rỗng → roadmap chuẩn theo level
    cvText: str | None = None


class RoadmapLesson(BaseModel):
    title: str


class RoadmapMilestone(BaseModel):
    title: str
    focusCriteria: list[str]
    lessons: list[RoadmapLesson]


class GenerateRoadmapResponse(BaseModel):
    milestones: list[RoadmapMilestone]


class GenerateLessonTheoryRequest(BaseModel):
    jobCategory: str
    level: str
    lessonTitle: str
    focusCriteria: list[str]
    weaknesses: list[str] | None = None


class GenerateLessonTheoryResponse(BaseModel):
    theoryMarkdown: str          # tiếng Việt, có ví dụ


class CriterionProgress(BaseModel):
    criterionName: str
    startPct: float | None = None
    endPct: float
    levelThreshold: float
    passed: bool


class SummarizeRoadmapRequest(BaseModel):
    jobCategory: str
    level: str
    criteriaProgress: list[CriterionProgress]


class SummarizeRoadmapResponse(BaseModel):
    strengths: list[str]
    weaknesses: list[str]
    improvements: list[str]
    overallComment: str