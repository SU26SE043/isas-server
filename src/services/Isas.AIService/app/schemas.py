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


class CriterionRef(BaseModel):
    """1 tiêu chí NỘI DUNG mà câu hỏi CÓ THỂ nhắm tới (chấm-theo-phạm-vi).

    Trước đây mọi câu trả lời bị chấm trên CẢ 7 tiêu chí bất kể câu hỏi hỏi gì: đo trên deploy,
    một câu về "xoay vòng refresh token" vẫn bị chấm tiêu chí *Thiết kế hệ thống & CSDL* (trọng số
    0.18) và ăn 2/5 CHỈ VÌ không được hỏi ⇒ cùng trình độ, bài trả lời câu hỏi hẹp được ~69/100
    còn bài trả lời câu "đại luận" được 91–97.

    Ở đây chỉ cần ``criterionId`` + ``name``: id để .NET map ngược (và để AIService DROP id lạ —
    chống bịa by-construction, mẫu ``citedChunkId`` của grounding và ``criterionId`` của C14),
    ``name`` để model hiểu tiêu chí nói về cái gì mà quyết định câu hỏi có nhắm tới nó không.
    KHÔNG mang ``maxScore``/``weight``: đây là bài toán GẮN NHÃN PHẠM VI, không phải chấm điểm.

    ⚠ 4 tiêu chí CÁCH NÓI (giao tiếp / trôi chảy / ngữ pháp / thuật ngữ) KHÔNG đi qua đây —
    chúng luôn chấm ở mọi câu nên .NET không gửi xuống, và model không có cửa nào loại chúng.
    """
    criterionId: str
    name: str


class LessonContextDto(BaseModel):
    """Chủ đề của ĐÚNG bài học mà buổi luyện này sinh ra từ (lộ trình B2C).

    Trước khi có nó, .NET chỉ gửi ``focusCriteria`` — tiêu chí của CHẶNG — nên mọi bài trong cùng
    một chặng cho lớp SINH đúng một đầu vào. Đo trên dev: 1 chặng có 4 bài (ôn ngôn ngữ · cấu trúc
    dữ liệu · thuật toán · OOP) dùng chung 3 tiêu chí, và một buổi của bài "tối ưu truy vấn SQL"
    nhận câu hỏi về xử lý lỗi API — chủ đề của bài KHÁC cùng chặng.
    """

    title: str
    # Mục lục bài giảng (đề mục ``##``), .NET đã cắt trần. Vắng = người học bấm Bắt đầu mà chưa mở
    # bài lần nào (lý thuyết sinh lazy) — hợp lệ, chỉ mất một lớp ngữ cảnh.
    outline: str | None = None


class SessionTopic(BaseModel):
    """TOP1-B4 — 1 đề tài của danh mục chủ đề luyện tập (TOP1) gắn cho buổi này.

    ``cvLevel``/``cvEvidence`` là ngữ cảnh TUỲ CHỌN suy từ CV ứng viên cho ĐÚNG đề tài này (vd
    mức độ thể hiện trong CV + trích dẫn bằng chứng) — optional-safe để một field thiếu không làm
    cả request 422, mẫu ``sourceUrl``/``sourceTitle`` của :class:`GroundingChunk`.
    """
    label: str
    cvLevel: str | None = None
    cvEvidence: str | None = None


class GenerateQuestionsRequest(BaseModel):
    jobCategory: str            # BA | BE | FE
    language: str = "vi"
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
    # Chấm-theo-phạm-vi — tập tiêu chí NỘI DUNG để model gắn nhãn cho từng câu hỏi.
    # Vắng/rỗng ⇒ KHÔNG gắn nhãn, response giữ nguyên xi shape cũ (Campaign B2B / mọi caller cũ).
    # ⚠ PHẢI khai tường minh: schema này không set model_config nên pydantic `extra='ignore'` sẽ
    # NUỐT IM LẶNG field quên khai — .NET gửi mà AI không thấy, không lỗi, không log, tính năng chỉ
    # đơn giản là không chạy (đúng lớp bug `focusCriteria` BC14 và `metricsVersion` 2026-08-05).
    criteria: list[CriterionRef] | None = None
    # SEN1 — CẤP ĐỘ ỨNG VIÊN do người dùng chọn (`Fresher|Junior|Middle|Senior`), dùng để hiệu chỉnh
    # độ khó của bộ CÂU GỐC. Trước SEN1, field này chỉ tới được `/decide-next` ⇒ người chọn *Senior*
    # nhận bộ câu gốc y hệt người chọn *Fresher*, mà câu gốc mới là thứ định khung cả buổi (INT-17b:
    # mỗi câu gốc còn kéo theo tối đa 3 tầng đào sâu quanh chính chủ đề nó mở ra).
    #
    # ⚠ Khai tường minh ở ĐÂY là nửa quyết định của tính năng: thiếu dòng này thì .NET vẫn gửi,
    # HTTP vẫn 200, không lỗi, không log — pydantic `extra='ignore'` chỉ đơn giản vứt field và
    # prompt không đổi một chữ. Chính lớp bug đó đã cắn repo 3 lần (`focusCriteria`/BC14 ·
    # `metricsVersion` · `adaptiveMaxQuestions` vs `maxQuestions`).
    #
    # Mặc định `"Junior"` = khớp mặc định của `practice_sessions.seniority` / `campaigns.seniority`
    # ở DB, nên caller cũ không gửi gì vẫn nhận đúng mức mà DB của chính họ đang ghi.
    # Giá trị lạ KHÔNG bị từ chối ở tầng schema (`str` chứ không phải Literal/Enum): xem
    # `app/seniority.normalize` — 422 ở đây sẽ thành 502 trên một buổi ĐÃ TRỪ CREDIT.
    seniority: str = "Junior"
    # Ngữ cảnh BÀI HỌC (chỉ đường bài học lộ trình gửi). Vắng ⇒ prompt GIỮ NGUYÊN XI cho mọi caller
    # cũ (luyện tự do, campaign B2B).
    #
    # ⚠ Khai tường minh ở ĐÂY là nửa quyết định của tính năng — y hệt `focusCriteria`/`seniority`
    # ngay trên: thiếu dòng này thì .NET vẫn gửi, HTTP vẫn 200, không lỗi, không log, và pydantic
    # `extra='ignore'` chỉ đơn giản vứt field ⇒ câu hỏi lặng lẽ bám CHẶNG thay vì bám BÀI. Đúng lớp
    # bug đã cắn repo 4 lần (`focusCriteria`/BC14 · `metricsVersion` · `adaptiveMaxQuestions` ·
    # `seniority`/SEN1).
    lessonContext: LessonContextDto | None = None
    # TOP1-B4 — danh mục đề tài của buổi (TOP1), chọn sẵn ở tầng .NET (TopicSelector, B3). Vắng ⇒
    # prompt GIỮ NGUYÊN XI cho mọi caller cũ. Có cả `lessonContext` lẫn `topics` ⇒ bài học THẮNG
    # (hẹp hơn — một bài cụ thể trong lộ trình), khối đề tài không xuất hiện.
    #
    # ⚠ Khai tường minh ở ĐÂY là nửa quyết định của tính năng — y hệt `lessonContext`/`seniority`
    # ngay trên: thiếu dòng này thì .NET vẫn gửi, HTTP vẫn 200, không lỗi, không log, và pydantic
    # `extra='ignore'` chỉ đơn giản vứt field. Đúng lớp bug đã cắn repo 4 lần (`focusCriteria`/BC14
    # · `metricsVersion` · `adaptiveMaxQuestions` · `seniority`/SEN1).
    topics: list[SessionTopic] | None = None


class GenerateQuestionsResponse(BaseModel):
    questions: list[str]
    # ADDITIVE — CHỈ có khi request cấp grounding (per-question). Campaign B2B bỏ qua field này
    # ⇒ không vỡ. Ungrounded → None (endpoint dùng exclude_none nên giữ nguyên shape cũ).
    citations: list[QuestionCitation] | None = None
    # ADDITIVE — mảng SONG SONG index-aligned với `questions`: phần tử i = danh sách criterionId
    # mà questions[i] nhắm tới (⊆ tập `criteria` đã cấp — provider đã drop id lạ + bỏ trùng).
    #
    # 🔴 TÊN KHOÁ LÀ HỢP ĐỒNG DÂY với .NET — đổi tên ở đây KHÔNG ném lỗi, nó chỉ làm .NET bind hụt
    # rồi lưu rỗng vĩnh viễn ⇒ mọi câu hỏi quay về bị chấm trên CẢ 7 tiêu chí như trước, không
    # triệu chứng nào ngoài "điểm dạo này lạ lạ". Thấy sai thì BÁO, đừng tự sửa một bên.
    #
    # Mảng song song (KHÔNG phải dict lồng trong từng câu hỏi) là để `questions: list[str]` giữ
    # nguyên kiểu — đúng mẫu `citations` đã có ở trên. None (ungrounded/không gắn nhãn) → endpoint
    # dùng exclude_none nên field BIẾN MẤT khỏi response, giữ shape cũ y hệt.
    targetCriteria: list[list[str]] | None = None


# ── Đề xuất tiêu chí có cấu trúc (Campaign C8) ──────────────────────────────
class SuggestCriteriaRequest(BaseModel):
    jobCategory: str
    language: str = "vi"
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


# ── MỐC ĐIỂM cho tiêu chí campaign (E9b) ────────────────────────────────────
# E9 (chấm NEO theo mức) đã chạy đủ ở cả hai đầu — C# gửi `Levels[]`, prompt in `• Mức n: …`,
# provider snap điểm về mức — nhưng B2B **chưa bao giờ có dữ liệu mức**: `campaign_criteria`
# không có cột mức nên mọi lượt chấm rơi vào dải mặc định `0..maxScore`, và prompt in ra
# `• Mức 3: Mức 3/5` rồi ngay dòng dưới bắt model "bám descriptor của mức đã chọn" — bám một
# tautology. Endpoint này đổ NỘI DUNG THẬT vào bộ máy sẵn có; nó KHÔNG ghi DB (GEN-4), chỉ trả
# về để HR xem/sửa rồi lưu qua đúng một cửa `PUT /campaign/{id}`.
class CriterionForLevels(BaseModel):
    """1 tiêu chí HR đã khai, cần AI đề xuất mốc điểm.

    ``maxScore`` là RÀNG BUỘC chứ không phải gợi ý: mốc cao nhất PHẢI đúng bằng nó (luật F13
    *"sampleAnswer ở mức ĐIỂM TỐI ĐA"* trỏ vào mức đó), và mốc thấp nhất phải là 0 (thiếu mốc 0
    thì bài TRỐNG snap về mốc thấp nhất còn lại — ứng viên không nói gì vẫn được điểm, và không
    lỗi nào nổ). Provider kiểm cả hai đầu, xem :meth:`GeminiProvider.suggest_criterion_levels`.
    """
    criterionId: str
    name: str
    description: str | None = None
    maxScore: int


class SuggestCriterionLevelsRequest(BaseModel):
    """⚠ Mọi field PHẢI khai tường minh — schema không set ``model_config`` nên pydantic
    ``extra='ignore'`` NUỐT IM LẶNG field quên khai (.NET gửi, HTTP 200, prompt không đổi một
    chữ). Đã cắn repo 4 lần: ``focusCriteria``/BC14 · ``metricsVersion`` · ``adaptiveMaxQuestions``
    · ``fullName``/BK28."""
    jobCategory: str
    language: str = "vi"
    seniority: str | None = None
    jdText: str | None = None
    # Số mốc mong muốn; None ⇒ để model tự chọn trong dải 3–6 (luật nằm trong prompt).
    levelCount: int | None = None
    criteria: list[CriterionForLevels]


class CriterionLevelItem(BaseModel):
    score: int
    descriptor: str


class CriterionLevels(BaseModel):
    """Mốc của MỘT tiêu chí — đã sort tăng, bỏ trùng, và ⊆ ``[0, maxScore]`` (provider lọc)."""
    criterionId: str
    levels: list[CriterionLevelItem]


class SuggestCriterionLevelsResponse(BaseModel):
    criteria: list[CriterionLevels]


# ── CHẤM THỬ (rubric preview) — employer tự kiểm chứng thước đo trước khi phát link ─────────
# Trái tim: 3 bài mẫu này được chấm bằng ĐÚNG `build_scoring_prompt` + `provider.score` mà ứng
# viên thật đi qua, mỗi bài MỘT lời gọi riêng. Gộp 3 bài vào một prompt sẽ biến bài toán thành
# XẾP HẠNG ⇒ thứ tự yếu-khá-giỏi đúng bất kể thước đo tốt hay không ⇒ tự bịt mắt đúng chỗ cần nhìn.
class PreviewLevelItem(BaseModel):
    score: int
    descriptor: str


class PreviewCriterion(BaseModel):
    """1 tiêu chí + mốc + MỨC KỲ VỌNG cho từng bài.

    ``expectedWeak/Good/Excellent`` do **code phía .NET** chọn (không phải model), nên mỗi tiêu
    chí có một mức biết-trước để đối chiếu ``expected vs actual`` — đây là cách DUY NHẤT đo được
    self-scoring bias khi cùng một model vừa viết bài vừa chấm bài. Ba field này CHỈ đi vào prompt
    SINH BÀI; chúng bị lột bỏ trước khi gọi ``score()`` để lượt chấm không hề biết đáp án.

    ``levels`` BẮT BUỘC ≥2: thiếu nó thì lượt chấm rơi về dải mặc định ``0..maxScore`` và bài
    kiểm chứng sẽ xác nhận một thước đo KHÁC thước đo HR vừa soạn.
    """
    criterionId: str
    name: str
    description: str | None = None
    maxScore: int
    weight: float
    levels: list[PreviewLevelItem]
    expectedWeak: int
    expectedGood: int
    expectedExcellent: int


class ScorePreviewRequest(BaseModel):
    jobCategory: str
    language: str = "vi"
    seniority: str | None = None
    question: str
    # Đáp án mẫu HR soạn (F13) — dùng để hiệu chỉnh bài "xuất sắc", KHÔNG phải để chép.
    sampleAnswer: str | None = None
    # Bài thứ 4 tuỳ chọn do HR tự dán — bài DUY NHẤT không do bộ chấm viết ra.
    customAnswer: str | None = None
    targetWordCount: int = 160
    criteria: list[PreviewCriterion]


class PreviewCriterionScore(BaseModel):
    criterionId: str
    score: float
    levelMatched: int
    reasoning: str


class PreviewSample(BaseModel):
    band: str                      # Weak | Good | Excellent | Custom
    answerText: str
    wordCount: int
    scores: list[PreviewCriterionScore]


class ScorePreviewResponse(BaseModel):
    samples: list[PreviewSample]
    # BK23 — con dấu bộ mảnh prompt ĐÃ dựng nên lượt chấm này. Admin sửa prompt F21 giữa hai lần
    # chấm thử là đổi thước đo mà KHÔNG đổi mốc nào; thiếu con dấu thì HR quy sai mọi thay đổi
    # cho việc mình vừa sửa mốc.
    promptVersion: int | None = None
    # Ba bài lệch độ dài quá ngưỡng sau khi đã sinh lại ⇒ KHÔNG giấu. Dải điểm đẹp lúc đó có thể
    # chỉ đang phản ánh "dài hơn thì điểm cao hơn" chứ không phải thước đo phân biệt được.
    lengthParityWarning: bool = False


# ── Phân tích CV (B2C BC6, D17) — sync HTTP, đường LUYỆN TẬP cá nhân ────────
#
# ⚠ Đường sàng CV B2B KHÔNG còn đi qua đây. Trước đây hai dòng dùng chung
# `analyze_cv` phân nhánh bằng `if criteria:`, nhưng chúng đã tách hẳn về bản chất:
# B2C = nhận xét hồ sơ giúp ứng viên sửa CV; B2B = HR technical screener (xem
# `screen_cv` + `JobNeed` bên dưới). Gộp lại còn ép hai khái niệm khác nhau dùng
# chung tên field `strengths` (`list[str]` ở đây vs `[{area,level,evidence}]` ở kia).
class AnalyzeCvRequest(BaseModel):
    cvText: str
    language: str = "vi"
    jdText: str | None = None
    jobCategory: str | None = None   # BA | BE | FE — optional (chỉ để cá nhân hoá nhận xét)
    # None/None = LEGACY; ít nhất một list khác None = đối chiếu theo từng requirement.
    # Các id này do service nghiệp vụ cấp, AIService chỉ được phép echo id đã nhận.
    mustHave: list[dict] | None = None
    niceToHave: list[dict] | None = None
    # ⚠ PHẢI nhận `None`: .NET gửi `grounding: null` ở đường phân tích CV THƯỜNG (không
    # requirement) vì tham số mặc định là null và `JsonContent.Create` KHÔNG bỏ null.
    # Khai `= []` (non-nullable) làm pydantic trả 422 ⇒ InterviewService map thành 502
    # ⇒ MỌI lượt phân tích CV thường hỏng, im lặng với người dùng. Đo được: lượt
    # legacy-mode cuối cùng trên production là 17/08 — đúng một ngày trước hai thay
    # đổi ngày 18/08 dựng nên cái bẫy này.
    grounding: list[GroundingChunk] | None = None


class JdMatch(BaseModel):
    score: int                # 0-100
    matchedSkills: list[str]
    missingSkills: list[str]


class AnalyzeCvResponse(BaseModel):
    summary: str
    strengths: list[str]
    weaknesses: list[str]
    suggestions: list[str]
    # Trình độ nghề nghiệp CV chứng minh được. `None` = KHÔNG đủ căn cứ — đo được 87% CV thật
    # không ghi trình độ ở đâu, nên đây là câu trả lời hạng nhất chứ không phải fallback.
    # ⚠ `response_model_exclude_none=True` ở route ⇒ `None` bị XOÁ khỏi JSON, không trả `null`.
    currentLevel: str | None = None
    jdMatch: JdMatch | None = None   # chỉ có khi request có jdText
    requirementMatches: list["CvRequirementMatch"] | None = None
    cvSections: list["CvSectionAnchor"] | None = None
    # Nguồn tri thức dùng cho suggestions; không phải citation chứng minh mức chấm.
    citations: list[GroundingChunk] | None = None


REQUIREMENT_PRIORITIES = ("MustHave", "NiceToHave")


class JdRequirement(BaseModel):
    """Một requirement AI tách từ JD; output bước đề xuất, chưa có id bền.

    ``citations`` là tài liệu chuẩn ngành lấy từ kho tri thức (Qdrant) — KHÔNG phải trích từ JD
    của user. ``jdQuote`` mới là câu NGUYÊN VĂN trong ``jdText`` sinh ra requirement này, để user
    kiểm chứng "câu này lấy từ đâu trong JD của tôi". AIService xác minh quote thật sự là substring
    của ``jdText`` (gộp whitespace, không phân biệt hoa thường); không phải ⇒ ``None`` — cùng kỷ
    luật chống bịa by-construction đang dùng cho ``chunkId``. ``None`` là hợp lệ (FE ẩn tính năng).
    """
    text: str
    citations: list[GroundingChunk] = []
    jdQuote: str | None = None


class SuggestJdRequirementsRequest(BaseModel):
    jdText: str
    jobCategory: str
    language: str = "vi"
    grounding: list[GroundingChunk] = []


class SuggestJdRequirementsResponse(BaseModel):
    mustHave: list[JdRequirement]
    niceToHave: list[JdRequirement]


class CvRequirementMatch(BaseModel):
    requirementId: str
    priority: str
    text: str
    level: str
    evidence: str


class CvSectionAnchor(BaseModel):
    title: str
    kind: str
    startsWith: str


# Các model response ở trên dùng forward reference để giữ nhóm schema liền mạch.
AnalyzeCvResponse.model_rebuild()


# ── Sàng CV B2B — HR technical screener ─────────────────────────────────────
# Vai: người sàng lọc kỹ thuật, KHÔNG phải máy chấm điểm. Nguyên tắc xuyên suốt:
# model chỉ được giao việc nó làm được — ĐỌC CV rồi trích bằng chứng; mọi con số
# dùng để xếp hạng đều do code tính từ bằng chứng đó.
#
# Vì sao không tái dùng `campaign_criteria` như trước: đó là rubric chấm CÂU TRẢ LỜI
# NÓI của buổi phỏng vấn ("Giao tiếp & Tiếng Anh", mức neo "1-4 điểm (Kém)…"). CV là
# giấy, không quan sát được mấy thứ đó ⇒ model đoán (đo trên prod: hai ứng viên khác
# hẳn nhau đều nhận đúng 7/10 ở "Giao tiếp & Tiếng Anh"). Tiêu chí campaign giữ
# nguyên vai trò của nó ở đường chấm phỏng vấn.

JOB_NEED_CATEGORIES = ("Technical", "WorkStyle", "Communication", "Growth")
NEED_LEVELS = ("Strong", "Partial", "Weak")
VERIFICATION_RISKS = ("Low", "Medium", "High")

# Tập giá trị hợp lệ của `AnalyzeCvResponse.currentLevel`. Khai LẠI ở đây thay vì import
# `app.seniority.LEVELS` để giữ `schemas.py` là module dữ liệu thuần (hiện chỉ phụ thuộc
# pydantic) — `seniority` kéo theo `prompt_registry` → `config`. Trôi khỏi nhau thì
# `test_cv_current_level.py` đỏ.
CV_CURRENT_LEVELS = ("Fresher", "Junior", "Middle", "Senior")

# Câu bắt buộc khi không tìm thấy bằng chứng. Là HẰNG SỐ chứ không phải câu model tự
# viết: nó phân biệt "đã tìm và không thấy" với "quên đánh giá", và HR đọc bảng thấy
# đúng một câu duy nhất thay vì mười cách diễn đạt khác nhau.
NO_EVIDENCE = "Không thấy bằng chứng"


class JobNeed(BaseModel):
    """Bước 1 — 1 nhu cầu công việc suy từ JD.

    Materialize **một lần cho cả campaign** (lúc publish), KHÔNG suy lại theo từng CV:
    bước này chỉ đọc JD chứ không đọc CV, nên nó là thuộc tính của campaign. Suy lại
    mỗi CV thì không gì buộc hai lần đọc ra cùng bộ nhu cầu ⇒ hai ứng viên cùng
    campaign bị đo bằng hai cái thước khác nhau rồi xếp chung một bảng — đúng thứ bất
    công mà CAMP-10 chặn ở đường phỏng vấn.

    ``needId`` để đối chiếu nhu cầu ↔ đánh giá; id nào model trả mà không có ở đây là
    id BỊA → bị drop (AI-3), y hệt cách `criterionId` từng được canh.
    """
    needId: str
    category: str   # ∈ JOB_NEED_CATEGORIES
    text: str


class SuggestJobNeedsRequest(BaseModel):
    jdText: str
    jobCategory: str | None = None
    language: str = "vi"


class SuggestJobNeedsResponse(BaseModel):
    needs: list[JobNeed]


# ── Phân tích GitHub repository (B2C BC18) ─────────────────────────────────
class AnalyzeRepoRequest(BaseModel):
    repoDigest: str
    language: str = "vi"
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


class CriterionEvidence(BaseModel):
    """BE-5 — bằng chứng HÀNH VI cho 1 tiêu chí yếu: Reasoning (E11, trích NGUYÊN VĂN lời ứng
    viên) của answer điểm THẤP NHẤT. .NET đã tải + cắt trần sẵn (RoadmapEvidenceLoader: ≤3 tiêu
    chí × ≤3 answer/tiêu chí)."""
    criterionName: str
    reasoning: list[str]


class GenerateRoadmapRequest(BaseModel):
    jobCategory: str
    language: str = "vi"
    level: str                                     # Fresher | Junior | Middle | Senior
    weaknesses: list[WeaknessScore] | None = None  # từ session_criterion_scores; rỗng → roadmap chuẩn theo level
    # 🔴 `cvText` ĐÃ BỊ GỠ — đừng khai lại. Đo trên production: roadmap có CV và không CV cho tên
    # chặng không phân biệt được, nhóm có CV còn nêu công nghệ cụ thể ÍT hơn (8,6% vs 12,1%). CV
    # thô là đầu vào SAI HÌNH DẠNG cho một bài toán sinh *cấu trúc giáo trình*. Phần CV đóng góp
    # được nay đi qua `cvAnalysisSummary` (bản tinh luyện) và `currentLevel` (sàn trình độ).
    # Trình độ HIỆN TẠI suy từ CV (khác `level` = MỤC TIÊU người dùng chọn). `None` = CV không đủ
    # căn cứ ⇒ không có sàn. ⚠ PHẢI khai tường minh — cùng bẫy `extra='ignore'` nêu ngay dưới.
    currentLevel: str | None = None
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
    # BE-1 — tiêu chí năng lực THẬT của (jobCategory, language), cùng shape `CriterionRef` dùng cho
    # chấm-theo-phạm-vi. Milestone.focusCriteria CHỈ được chọn tên trong tập này (sao chép nguyên
    # văn); vắng/rỗng ⇒ không ràng buộc gì thêm (hành vi cũ). ⚠ PHẢI khai tường minh ở đây — lệch
    # với comment `focus`/`cvAnalysisSummary` ngay trên: schema này KHÔNG set model_config, nên
    # pydantic `extra='ignore'` sẽ NUỐT IM LẶNG field quên khai (đúng lớp bug BC14/F2b
    # `focusCriteria`, và là chính lý do đo được chỉ 7% focusCriteria khớp rubric thật trên prod).
    criteria: list[CriterionRef] | None = None
    # BE-4 — độ dài roadmap candidate CHỌN (Quick=2 milestone×2 lesson · Standard=4×3, xem
    # app.roadmap_quality). Mặc định "Standard" giữ hành vi client cũ (chưa gửi field này). ⚠ PHẢI
    # khai tường minh — cùng bẫy `extra='ignore'` nêu ở `criteria` ngay trên: thiếu dòng này thì
    # .NET gửi `scope` mà pydantic nuốt im lặng, mọi request luôn rơi về mặc định trong code.
    scope: str = "Standard"
    # BE-5 — bằng chứng (Reasoning E11) cho tiêu chí yếu, xem CriterionEvidence. Vắng/rỗng ⇒ không
    # ràng buộc gì thêm (hành vi cũ). ⚠ PHẢI khai tường minh — cùng bẫy `extra='ignore'` ở trên.
    evidence: list[CriterionEvidence] | None = None
    # Chế độ lộ trình: "LevelUp" (mặc định — tiến lên cấp mục tiêu, hành vi cũ) hoặc
    # "Reinforce" (ôn lại: giữ nguyên trình độ, bám điểm yếu đo được, nghiêng về lý thuyết
    # giải thích chỗ đã sai). Xem app.roadmap_mode. ⚠ PHẢI khai tường minh — cùng bẫy
    # `extra='ignore'` đã nêu ở `criteria`/`scope`: thiếu dòng này thì .NET gửi `mode` mà
    # pydantic NUỐT IM LẶNG, mọi lộ trình ôn tập âm thầm được sinh như LevelUp.
    mode: str = "LevelUp"


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
    language: str = "vi"
    level: str
    lessonTitle: str
    focusCriteria: list[str]
    weaknesses: list[str] | None = None
    # RAG grounding (Contract 2) — vắng/rỗng → ungrounded. Khai tường minh (BC14).
    grounding: list[GroundingChunk] | None = None
    # BE-5 — bằng chứng (Reasoning E11) cho tiêu chí yếu, xem CriterionEvidence ở trên. Vắng/rỗng
    # ⇒ không ràng buộc gì thêm (hành vi cũ). ⚠ Khai tường minh — mẫu `grounding`/`weaknesses`.
    evidence: list[CriterionEvidence] | None = None
    # Chế độ lộ trình: "LevelUp" (mặc định — tiến lên cấp mục tiêu, hành vi cũ) hoặc
    # "Reinforce" (ôn lại: giữ nguyên trình độ, bám điểm yếu đo được, nghiêng về lý thuyết
    # giải thích chỗ đã sai). Xem app.roadmap_mode. ⚠ PHẢI khai tường minh — cùng bẫy
    # `extra='ignore'` đã nêu ở `criteria`/`scope`: thiếu dòng này thì .NET gửi `mode` mà
    # pydantic NUỐT IM LẶNG, mọi lộ trình ôn tập âm thầm được sinh như LevelUp.
    mode: str = "LevelUp"


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
    language: str = "vi"
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
    language: str = "vi"
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

class CriterionEvidenceState(BaseModel):
    """Trạng thái bằng chứng theo tiêu chí — .NET là chủ state, đây chỉ là bản chụp để QUYẾT ĐỊNH.

    ⚠ `deepCount` CỐ Ý KHÔNG khai ở đây, đừng thêm lại theo phản xạ "cho khớp .NET":
    (1) `build_decide_next_prompt` đọc đúng 5 field dưới đây và KHÔNG đọc nó ⇒ khai thêm chỉ tạo ra
        một field có tên mà không có ruột, và người sau sẽ tưởng prompt đang dùng nó;
    (2) nó là tín hiệu ĐỘ SÂU/NGÂN SÁCH, mà quyết định sản phẩm đã chốt là bằng chứng chỉ lái NỘI DUNG
        câu hỏi, KHÔNG lái độ dài buổi — nhét nó vào prompt là lái độ dài qua cửa sau;
    (3) ngân sách chuỗi vốn đã tới prompt bằng đường riêng và ĐÚNG hơn: `currentDepth`/`maxDepth`.
    .NET vẫn gửi `deepCount` cũng không sao — pydantic `extra='ignore'` bỏ qua, không 422.
    """
    criterionId: str
    name: str
    state: str = "UNKNOWN"
    evidenceFound: list[str] = []
    missingEvidence: list[str] = []


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
    seniority: str = "Junior"
    currentEvidenceState: list[CriterionEvidenceState] = []

    # INT-17b — ngữ cảnh CHUỖI đào sâu (mỗi câu gốc có chuỗi riêng, tối đa `maxDepth` tầng).
    # ⚠ PHẢI khai đủ: schema này không set model_config nên pydantic `extra='ignore'` sẽ NUỐT IM LẶNG
    # field quên khai — .NET gửi mà Python không thấy, không lỗi gì, prompt chạy như chưa có tính năng
    # (đúng lớp bug đã làm `focusCriteria` của BC14 hỏng suốt nhiều tuần).
    rootQuestion: str | None = None     # câu GỐC của chuỗi — mỏ neo chủ đề, giữ câu đào sâu không lạc đề
    currentDepth: int = 0               # câu vừa trả lời đang ở tầng mấy (0 = chính câu gốc)
    maxDepth: int = 0                   # trần tầng cho MỖI câu gốc; 0 = chế độ cũ (ngân sách theo buổi)
    otherTopics: list[str] = []         # tên các câu gốc KHÁC của buổi → đừng hỏi trùng chủ đề đã có


class DeliveryMetrics(BaseModel):
    """F11 (FR06) — chỉ số CÁCH NÓI đo từ audio (xem app/fluency.py).

    ⚠ ``fillerCount`` là mức TỐI THIỂU: Whisper thường nuốt bớt từ đệm nên số thật cao hơn.
    Chỉ số thời gian (``longestPauseSec``/``silenceRatio``/``speechRateWpm``) đáng tin hơn.

    🔴 **Mọi khoá `fluency.DeliveryMetrics.to_dict()` sinh ra PHẢI được khai ở đây.** Pydantic mặc
    định `extra='ignore'` nên khoá không khai bị **nuốt IM LẶNG** — không lỗi, không cảnh báo, chỉ
    là field rụng mất trên đường `/decide-next`. Đúng lớp bug đã làm `focusCriteria` (BC14) hỏng
    âm thầm, và đã tái diễn với `metricsVersion` (thêm vào `to_dict()` 2026-08-05 nhưng quên khai
    ở đây ⇒ cột `practice_answers.metrics_version` NULL trên production dù mọi test đều xanh —
    e2e mới bắt được). `test_fluency_f11.py` nay khoá bất biến này bằng cách so hai bộ khoá."""
    metricsVersion: int | None = None
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
    targetCriterionId: str | None = None
    evidenceFound: list[str] | None = None
    missingEvidence: list[str] | None = None
    newEvidenceState: str | None = None

    # Vì sao bản chép KHÔNG DÙNG ĐƯỢC: "no_speech" (VAD không thấy vùng tiếng nói nào — không gọi
    # engine nào) / "junk_transcript" (cả từ xa lẫn cục bộ đều ra chuỗi rác máy sinh). None = bình
    # thường. .NET đọc field này để đánh answer `Skipped` và KHÔNG publish job chấm.
    #
    # 🔴 TÊN KHOÁ LÀ HỢP ĐỒNG DÂY (`DecideNextResult.RejectReason`). Quên KHAI field ở schema này
    # thì pydantic NUỐT IM LẶNG giá trị `main.py` truyền vào — đúng lớp bug `focusCriteria` (BC14)
    # và `metricsVersion`, và hậu quả ở đây là hệ quay lại chấm điểm cho sự im lặng.
    rejectReason: str | None = None


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
    language: str = "vi"


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
