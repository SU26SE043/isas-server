from pydantic import BaseModel


class GenerateQuestionsRequest(BaseModel):
    jobCategory: str            # BA | BE | FE
    cvText: str | None = None
    jdText: str | None = None
    # F2b — số câu do ứng viên chọn (B2C). Bỏ trống → settings.question_count (hành vi cũ = 5).
    # Trần thực thi ở .NET (1..20); ở đây chỉ nhận giá trị đã được kiểm.
    count: int | None = None
    # 🐛 F2b — .NET GỬI field này từ BC14 (câu hỏi bài học roadmap bám tiêu chí milestone) nhưng schema
    # chưa bao giờ khai ⇒ pydantic `extra='ignore'` NUỐT IM LẶNG: không lỗi, không log, câu hỏi chỉ đơn
    # giản là không bám tiêu chí như thiết kế. Khai ra + đưa vào prompt thì mới thật sự có tác dụng.
    focusCriteria: list[str] | None = None


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


class LessonResource(BaseModel):
    """F15 — 1 tài liệu học gợi ý cho bài học.

    ``url`` optional VÌ CÓ CHỦ ĐÍCH: link do LLM sinh chỉ được giữ khi tên miền
    nằm trong allowlist (app/resources.py). Host lạ → url=None, mục vẫn còn tên
    để người học tự tra. FE phải gắn nhãn "chưa kiểm chứng" khi có url.
    """
    title: str
    type: str                    # Doc | Course | Book | Video | Article
    publisher: str | None = None
    url: str | None = None


class GenerateLessonTheoryResponse(BaseModel):
    theoryMarkdown: str          # tiếng Việt, có ví dụ
    resources: list[LessonResource] = []   # F15 — tài liệu học gợi ý (có thể rỗng)


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


# ── Nhận xét chung buổi luyện B2C (BC10) — sync best-effort, stateless ───────
class CriteriaScore(BaseModel):
    name: str
    percentage: float          # 0-100 (điểm tiêu chí quy về %)
    needsImprovement: bool     # từ session_criterion_scores (BC9)


class SummarizeSessionRequest(BaseModel):
    jobCategory: str
    overallScore: float
    criteriaScores: list[CriteriaScore]   # rỗng vẫn ra nhận xét tổng quát theo overallScore


class SummarizeSessionResponse(BaseModel):
    overallComment: str        # tiếng Việt, vài câu: tổng quan mạnh/yếu + hướng cải thiện


# ── Phỏng vấn THÍCH ỨNG (adaptive interview) — decide-next, stateless ────────
# InterviewService (chủ state) gọi sau mỗi câu trả lời: gửi audio key (hoặc text)
# + lịch sử Q&A + tiêu chí → AIService transcribe + QUYẾT ĐỊNH hành động kế tiếp
# (follow_up | clarify | new_question | end). AIService KHÔNG ghi DB (GEN-4);
# mọi state hội thoại nằm trong request (stateless).
class DecideTurn(BaseModel):
    question: str
    answer: str | None = None      # transcript câu trả lời (None nếu chưa/không trả lời)
    kind: str = "Seed"             # Seed | FollowUp | Clarify | NewQuestion


class DecideCriterion(BaseModel):
    name: str
    description: str | None = None  # để follow-up NEO cùng năng lực → công bằng B2B


class DecideNextRequest(BaseModel):
    jobCategory: str
    audioObjectKey: str | None = None   # ưu tiên: transcribe tại đây (single-source transcript)
    answerText: str | None = None       # fallback nếu caller đã có transcript (dùng cho test)
    language: str = "vi"
    currentQuestion: str                # câu hỏi ứng viên vừa trả lời
    history: list[DecideTurn] = []      # các lượt Q&A trước (stateless — caller truyền)
    askedCount: int = 0                 # tổng số câu đã hỏi (seed + thích ứng)
    followUpCount: int = 0              # số câu thích ứng đã thêm
    maxQuestions: int = 0               # 0 = không trần cứng
    maxFollowUps: int = 0               # 0 = không trần cứng
    criteria: list[DecideCriterion] = []


class DeliveryMetrics(BaseModel):
    """F11 (FR06) — chỉ số CÁCH NÓI đo từ mốc thời gian Whisper (xem app/fluency.py).

    ⚠ ``fillerCount`` là mức TỐI THIỂU: Whisper thường nuốt bớt từ đệm nên số thật cao hơn.
    Chỉ số thời gian (``longestPauseSec``/``silenceRatio``/``speechRateWpm``) đáng tin hơn."""
    audioSec: float = 0.0
    speechSec: float = 0.0
    wordCount: int = 0
    speechRateWpm: float = 0.0          # âm tiết/phút (tiếng Việt đơn âm tiết — xem fluency.py)
    longestPauseSec: float = 0.0
    pauseCount: int = 0
    silenceRatio: float = 0.0
    fillerCount: int = 0
    fillerPer100Words: float = 0.0
    fillerBreakdown: dict[str, int] = {}


class DecideNextResponse(BaseModel):
    action: str                         # follow_up | clarify | new_question | end
    nextQuestion: str | None = None     # None ⇔ action == end
    transcript: str | None = None       # echo khi transcribe từ audioObjectKey (single-source)
    reason: str | None = None

    # F11 — chỉ số đo TRONG CÙNG lượt transcribe đồng bộ này. Phải trả ở đây, nếu không thì buổi
    # ADAPTIVE mất chỉ số (worker bỏ Whisper khi job đã mang transcript) còn buổi TĨNH lại có →
    # hỏng ÂM THẦM, không lỗi gì. None = không đo được (fallback answerText / audio rỗng).
    deliveryMetrics: DeliveryMetrics | None = None


# ── Đối chiếu khuôn mặt (SEC-2/3) — sync HTTP, CampaignService gọi khi giám sát ──────
class FaceVerifyRequest(BaseModel):
    referenceImageKey: str        # S3 key ảnh tham chiếu (đã đăng ký/consent)
    liveImageKey: str             # S3 key ảnh chụp live trong lúc thi
    threshold: float | None = None  # ngưỡng cosine coi là khớp; None → face_match_threshold


class FaceVerifyResponse(BaseModel):
    faceCount: int                # số mặt trên ảnh LIVE
    match: bool                   # score ≥ threshold VÀ đúng 1 mặt
    score: float                  # cosine similarity ref↔live (0 nếu không so được)
    signals: list[str]            # ⊂ no_face / multiple_faces / face_mismatch (cờ cho HR)


# ── TTS: đọc câu hỏi thành tiếng — sync HTTP, InterviewService gọi (máy-máy) ────────
class TtsRequest(BaseModel):
    text: str                     # NỘI DUNG câu hỏi cần đọc (dữ liệu, không phải lệnh — AI-4)
    voice: str | None = None      # giọng dựng sẵn; None → settings.tts_voice
