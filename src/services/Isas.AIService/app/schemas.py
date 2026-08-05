from pydantic import BaseModel


# ── RAG GROUNDING (Contract 2) ──────────────────────────────────────────────
# InterviewService truy hồi các đoạn tài liệu uy tín từ Qdrant rồi TRUYỀN VÀO request
# khi gọi lớp SINH (câu hỏi / lý thuyết / roadmap). AIService chèn chúng làm căn cứ
# nội dung + trả citation. ⚠ PHẢI khai tường minh trong mọi request có grounding — schema
# không set model_config nên pydantic `extra='ignore'` sẽ NUỐT IM LẶNG field quên khai
# (đúng bug BC14/F2b `focusCriteria`): .NET gửi mà AI không thấy, không lỗi, không log.
class GroundingChunk(BaseModel):
    """1 đoạn tài liệu tham chiếu do InterviewService truy hồi từ Qdrant.

    ``chunkId`` = định danh để model TRÍCH DẪN ngược (AIService drop id lạ ⇒ chống bịa
    by-construction). ``content`` = đoạn văn cấp vào prompt làm căn cứ. ``sourceUrl``/
    ``sourceTitle`` chỉ để hiển thị (W2 map từ payload Qdrant) → optional-safe để một field
    thiếu không làm cả request 422.
    """
    chunkId: str
    content: str
    sourceUrl: str | None = None
    sourceTitle: str | None = None


class QuestionCitation(BaseModel):
    """Citation THEO TỪNG câu hỏi (Contract CITATION, per-item).

    ``citedChunkIds`` ⊆ tập grounding đã cấp (AIService đã lọc id lạ). Rỗng = câu này không
    dựa nguồn nào → FE gắn nhãn ungrounded cho riêng câu đó."""
    questionIndex: int
    citedChunkIds: list[str]


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
    # RAG grounding (Contract 2) — vắng/rỗng → ungrounded (VẪN sinh, không citation).
    grounding: list[GroundingChunk] | None = None


class GenerateQuestionsResponse(BaseModel):
    questions: list[str]
    # ADDITIVE — CHỈ có khi request cấp grounding (per-question). Campaign B2B bỏ qua field này
    # ⇒ không vỡ. Ungrounded → None (endpoint dùng exclude_none nên giữ nguyên shape cũ).
    citations: list[QuestionCitation] | None = None


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
class CvCriterion(BaseModel):
    """C14 — 1 tiêu chí campaign gửi kèm để AI chấm khớp CV (`campaign_criteria`).

    Worker/endpoint KHÔNG tự đọc DB (GEN-4) → CampaignService đẩy sẵn tiêu chí xuống.
    ``criterionId`` để trả lại ĐÚNG id trong callback; id nào không có ở đây mà model
    trả về là id BỊA → bị drop (AI-3).
    """
    criterionId: str
    name: str
    description: str | None = None
    maxScore: int = 5


class CriterionMatch(BaseModel):
    """C14 — điểm khớp CV theo 1 tiêu chí. ``matchScore`` đã kẹp [0, maxScore] phía provider."""
    criterionId: str
    matchScore: float
    reasoning: str | None = None


class AnalyzeCvRequest(BaseModel):
    cvText: str
    jdText: str | None = None
    jobCategory: str | None = None   # BA | BE | FE — optional (chỉ để cá nhân hoá nhận xét)
    # C14 (B2B sàng CV) — có criteria ⇒ res thêm criterionMatches + overallMatchScore + trích xuất.
    # ⚠ PHẢI khai tường minh: schema này không set model_config nên pydantic `extra='ignore'` sẽ
    # NUỐT IM LẶNG field quên khai (đúng bug BC14/F2b `focusCriteria`) — .NET gửi mà AI không thấy,
    # không lỗi, không log, tính năng chỉ đơn giản là không chạy.
    criteria: list[CvCriterion] | None = None


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

    # ── C14 (B2B) — ADDITIVE, chỉ có khi request cấp `criteria[]` ────────────────────
    # Mặc định None (KHÔNG phải [] ) là CÓ CHỦ ĐÍCH: endpoint dùng response_model_exclude_none
    # nên None ⇒ field biến mất ⇒ đường B2C giữ NGUYÊN XI shape cũ (ai.md: "không có
    # jdText/criteria → bỏ jdMatch/criterionMatches/overallMatchScore"). Để `= []` thì B2C sẽ
    # bắt đầu trả `"criterionMatches": []` — đổi hợp đồng của một đường đang chạy.
    skills: list[str] | None = None
    yearsExperience: float | None = None
    education: list[str] | None = None
    criterionMatches: list[CriterionMatch] | None = None
    overallMatchScore: int | None = None   # 0-100


# ── Phân tích GitHub repository (B2C BC18) ─────────────────────────────────
class AnalyzeRepoRequest(BaseModel):
    repoDigest: str
    jobCategory: str | None = None
    jdText: str | None = None


class AnalyzeRepoResponse(BaseModel):
    summary: str
    techStack: list[str]
    strengths: list[str]
    weaknesses: list[str]
    suggestions: list[str]
    interviewTalkingPoints: list[str]
    jdMatch: JdMatch | None = None


# ── Roadmap ôn tập cá nhân hoá B2C (BC13, D20) — 3 endpoint sync, stateless ─
class WeaknessScore(BaseModel):
    criterionName: str
    percentage: float


class GenerateRoadmapRequest(BaseModel):
    jobCategory: str
    level: str                                     # Fresher | Junior | Middle | Senior
    weaknesses: list[WeaknessScore] | None = None  # từ session_criterion_scores; rỗng → roadmap chuẩn theo level
    cvText: str | None = None
    # BC17 — cá nhân hoá roadmap từ report cũ do ứng viên CHỌN + ô mô tả mong muốn. 3 field này là
    # free-text/tóm tắt do ứng viên/hệ thống cung cấp ⇒ bọc-làm-DỮ-LIỆU trong prompt (AI-4), KHÔNG
    # phải chỉ thị. ⚠ PHẢI khai đủ: schema này không set model_config nên pydantic `extra='ignore'`
    # sẽ NUỐT IM LẶNG field quên khai (đúng bug BC14/F2b `focusCriteria`) → .NET gửi mà AI không thấy.
    focus: str | None = None                       # ô ứng viên mô tả mong muốn định hướng (free-text)
    cvAnalysisSummary: str | None = None           # tóm tắt phân tích CV (BC7) ứng viên đã chọn
    priorRoadmapSummary: str | None = None         # tóm tắt roadmap/report trước ứng viên đã chọn
    # RAG grounding (Contract 2) — cấu trúc roadmap KHÔNG emit citation (Phase 1), nhưng nếu W2
    # cấp grounding thì nó được chèn làm căn cứ. Khai tường minh để pydantic không nuốt (BC14).
    grounding: list[GroundingChunk] | None = None


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
    # RAG grounding (Contract 2) — vắng/rỗng → ungrounded. Khai tường minh (BC14).
    grounding: list[GroundingChunk] | None = None


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
    # ADDITIVE (Contract 2) — chunkId (⊆ tập grounding đã cấp) mà lý thuyết dựa vào. Ungrounded →
    # None (endpoint exclude_none giữ shape cũ). Grounded-nhưng-không-cite → [] (ungrounded label).
    citedChunkIds: list[str] | None = None


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

    # INT-17b — ngữ cảnh CHUỖI đào sâu (mỗi câu gốc có chuỗi riêng, tối đa `maxDepth` tầng).
    # ⚠ PHẢI khai đủ: schema này không set model_config nên pydantic `extra='ignore'` sẽ NUỐT IM LẶNG
    # field quên khai — .NET gửi mà Python không thấy, không lỗi gì, prompt chạy như chưa có tính năng
    # (đúng lớp bug đã làm `focusCriteria` của BC14 hỏng suốt nhiều tuần).
    rootQuestion: str | None = None     # câu GỐC của chuỗi — mỏ neo chủ đề, giữ câu đào sâu không lạc đề
    currentDepth: int = 0               # câu vừa trả lời đang ở tầng mấy (0 = chính câu gốc)
    maxDepth: int = 0                   # trần tầng cho MỖI câu gốc; 0 = chế độ cũ (ngân sách theo buổi)
    otherTopics: list[str] = []         # tên các câu gốc KHÁC của buổi → đừng hỏi trùng chủ đề đã có


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

    # Con dấu engine đã CHÉP RA `transcript` ("whisper-1" / "gemini-2.5-flash" / "local:small").
    # 🔴 TÊN KHOÁ LÀ HỢP ĐỒNG DÂY với .NET — đổi tên ở đây KHÔNG ném lỗi, nó chỉ làm .NET bind hụt
    # rồi lưu NULL vĩnh viễn (đúng lớp bug `focusCriteria` bị pydantic nuốt và `adaptiveMaxQuestions`
    # vs `maxQuestions`). Thấy sai thì BÁO, đừng tự sửa một bên.
    #
    # Cần thiết vì đường chép lời nay có DỰ PHÒNG: khi nhà cung cấp từ xa hỏng, bản chép lặng lẽ
    # rơi về Whisper cục bộ (lỗi từ 4,2% so với 0,7%) mà nhìn từ ngoài hai bản giống hệt nhau.
    # None = không đo được (nhánh answerText, không có audio để chép).
    transcriptEngine: str | None = None


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


# ── EMBEDDING (RAG grounding, Contract 1) — sync HTTP, InterviewService gọi (máy-máy) ──
# Stateless (GEN-4): chỉ sinh vector, KHÔNG ghi kho nào (InterviewService upsert vào Qdrant).
class EmbedRequest(BaseModel):
    texts: list[str]              # batch text cần embed (chunk lúc ingest / query lúc truy hồi)
    # RETRIEVAL_DOCUMENT (ingest) | RETRIEVAL_QUERY (truy vấn) — cùng model, không gian vector chung.
    taskType: str


class EmbedResponse(BaseModel):
    vectors: list[list[float]]    # 1 vector/text, cùng thứ tự texts đầu vào
    dim: int                      # số chiều (khớp collection Qdrant `knowledge`)
    model: str                    # model đã dùng (snapshot để đối soát nếu đổi embedder sau này)
