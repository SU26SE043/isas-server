from fastapi import FastAPI, HTTPException, UploadFile, File, APIRouter, Header, Response
import hmac
import os
import tempfile
import asyncio
from contextlib import asynccontextmanager
from app.schemas import (
    GenerateQuestionsRequest, GenerateQuestionsResponse, QuestionCitation,
    SuggestCriteriaRequest, SuggestCriteriaResponse, CriterionItem,
    SuggestCriterionLevelsRequest, SuggestCriterionLevelsResponse, CriterionLevels,
    ScorePreviewRequest, ScorePreviewResponse, PreviewSample, PreviewCriterionScore,
    PreviewCriterion,
    AnalyzeCvRequest, AnalyzeCvResponse, AnalyzeRepoRequest, AnalyzeRepoResponse, JdMatch,
    CvRequirementMatch, CvSectionAnchor, GroundingChunk,
    JobNeed, SuggestJobNeedsRequest, SuggestJobNeedsResponse,
    GenerateRoadmapRequest, GenerateRoadmapResponse, RoadmapMilestone, RoadmapLesson,
    GenerateLessonTheoryRequest, GenerateLessonTheoryResponse,
    SummarizeRoadmapRequest, SummarizeRoadmapResponse,
    SummarizeSessionRequest, SummarizeSessionResponse,
    FaceVerifyRequest, FaceVerifyResponse,
    DecideNextRequest, DecideNextResponse, DeliveryMetrics,
    TtsRequest,
    EmbedRequest, EmbedResponse,
)
from app.providers import gemini as gemini_module
from app.providers.gemini import GeminiProvider
from app.transcriber import Transcriber
from app.face_verify import FaceVerifier
from app.config import settings
from app import storage, audio, threadpool, tts

@asynccontextmanager
async def _lifespan(_app: FastAPI):
    # Đặt trần thread cho công việc CHẶN. Mặc định cấu hình là 0 = giữ nguyên hành vi asyncio,
    # nên thay đổi này ship ra là no-op; nới bằng env THREAD_POOL_MAX_WORKERS sau khi đo.
    # Không cần tự shutdown executor: uvicorn chạy qua `asyncio.run`, mà nó gọi
    # `loop.shutdown_default_executor()` lúc đóng.
    threadpool.apply(asyncio.get_running_loop(), settings.thread_pool_max_workers)
    yield


app = FastAPI(title="ISAS AI Service", lifespan=_lifespan)
transcriber = Transcriber()
provider = GeminiProvider()
face_verifier = FaceVerifier()


async def _call_with_language(language: str, method, *args, **kwargs):
    """Keep the deployed Vietnamese provider call shape byte-for-byte compatible.

    Test doubles and older provider implementations do not accept ``language``; only the new
    English path needs the additional keyword.
    """
    if language == "vi":
        return await method(*args, **kwargs)
    return await method(*args, language=language, **kwargs)


def _valid_internal_token(token: str | None) -> bool:
    """So khớp HẰNG-THỜI-GIAN X-Internal-Token với cấu hình (GEN-7 hardening).

    Fail-closed: token chưa cấu hình hoặc header thiếu → từ chối.

    Q2 — áp cho MỌI endpoint trừ /health. Trước đó chỉ 5/13 endpoint gọi hàm này, nên nhóm
    endpoint SINH (generate-questions, suggest-criteria, analyze-cv, roadmap, lesson-theory,
    summarize-*) và /transcribe để trần: một POST ẩn danh từ Internet gọi được /generate-questions
    và tính tiền thẳng vào tài khoản Gemini của dự án (đã tái hiện, ai_usage_logs ghi lại). Cô lập
    mạng KHÔNG phải lá chắn — gateway có tuyến /api/v1/ai/** trong Development, và không có
    rate-limit nào phủ nhóm này.
    """
    expected = settings.internal_token
    if not expected or not token:
        return False
    return hmac.compare_digest(token, expected)


# 1. Định nghĩa Router
router = APIRouter(prefix="/api/v1")

@router.get("/health")
async def health():
    return {"status": "ok"}

@router.post("/generate-questions", response_model=GenerateQuestionsResponse,
             response_model_exclude_none=True)
async def generate_questions(req: GenerateQuestionsRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    try:
        # RAG grounding (Contract 2) — chuyển sang list[dict] cho provider; vắng → ungrounded.
        grounding = [g.model_dump() for g in req.grounding] if req.grounding else None
        # Chấm-theo-phạm-vi — vắng ⇒ None (KHÔNG phải []): provider rẽ nhánh theo truthiness, và
        # response_model_exclude_none bỏ hẳn targetCriteria ⇒ caller cũ giữ nguyên shape.
        criteria = [c.model_dump() for c in req.criteria] if req.criteria else None
        # SEN1 — cấp độ ứng viên phải tới được LỚP SINH, không chỉ `/decide-next`. Truyền bằng
        # TỪ KHOÁ: `_call_with_language` chèn `language=` vào kwargs, nên mọi thứ sau `criteria`
        # phải là keyword, và keyword thì không lệch khi provider thêm tham số về sau.
        # Quên dòng này thì schema có khai, .NET có gửi, HTTP vẫn 200 — prompt chỉ đơn giản không
        # đổi một chữ (cùng lớp bug `targetCriteria` ngay dưới đây và `metricsVersion` 2026-08-05).
        result = await _call_with_language(req.language, provider.generate,
            req.jobCategory, req.cvText, req.jdText, req.count, req.focusCriteria, grounding,
            criteria, seniority=req.seniority)
        # citations=None (ungrounded) → response_model_exclude_none bỏ field → shape cũ cho Campaign B2B.
        citations = ([QuestionCitation(**c) for c in result.citations]
                     if result.citations is not None else None)
        # Quên dòng targetCriteria dưới đây thì pydantic KHÔNG lỗi, field chỉ đơn giản không bao giờ
        # ra wire — .NET nhận rỗng và mọi câu hỏi lặng lẽ quay về bị chấm trên CẢ 7 tiêu chí
        # (cùng lớp bug `metricsVersion` rụng ở `DeliveryMetrics` 2026-08-05, `fullName` ở BK28).
        return GenerateQuestionsResponse(questions=result.questions, citations=citations,
                                         targetCriteria=result.target_criteria)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh câu hỏi: {ex}")

@router.post("/suggest-criteria", response_model=SuggestCriteriaResponse)
async def suggest_criteria(req: SuggestCriteriaRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    try:
        items = await _call_with_language(req.language, provider.suggest_criteria,
            req.jobCategory, req.jdText, req.criteriaText, req.count)
        return SuggestCriteriaResponse(criteria=[CriterionItem(**c) for c in items])
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi đề xuất tiêu chí: {ex}")

@router.post("/suggest-criterion-levels", response_model=SuggestCriterionLevelsResponse)
async def suggest_criterion_levels(req: SuggestCriterionLevelsRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    """E9b — đề xuất MỐC ĐIỂM cho tiêu chí campaign. KHÔNG ghi DB (GEN-4).

    CampaignService gọi khi HR bấm "AI gợi ý mốc"; kết quả trả thẳng về cho HR xem/sửa, việc LƯU
    đi qua đúng một cửa `PUT /campaign/{id}` (giữ audit + luật bump version ở một chỗ).

    Lỗi ⇒ 502, **KHÔNG fallback dải mặc định** — xem docstring provider: mốc bịa sẽ được HR tin là
    do AI viết, mà "không có mốc" vốn là trạng thái hợp lệ nên fail-loud không chặn ai."""
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.criteria:
        raise HTTPException(status_code=400, detail="criteria không được rỗng")
    try:
        # `seniority` truyền bằng TỪ KHOÁ: `_call_with_language` chèn `language=` vào kwargs nên
        # mọi thứ sau `level_count` phải là keyword (mẫu `/generate-questions` SEN1).
        items = await _call_with_language(req.language, provider.suggest_criterion_levels,
            req.jobCategory, [c.model_dump() for c in req.criteria],
            req.jdText, req.levelCount, seniority=req.seniority)
        return SuggestCriterionLevelsResponse(
            criteria=[CriterionLevels(**i) for i in items])
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi đề xuất mốc điểm: {ex}")


# E9b — 3 field mức-kỳ-vọng CHỈ dùng cho lượt SINH bài. Chúng phải bị lột bỏ trước khi gọi
# `score()`: để lọt là mách đáp án cho chính bộ chấm ("bài này đáng mức 2"), và mọi con số
# expected-vs-actual sau đó thành vô nghĩa — mà nó lại trông rất thuyết phục.
_PREVIEW_EXPECTED_FIELDS = ("expectedWeak", "expectedGood", "expectedExcellent")


def build_preview_scoring_criteria(criteria: list[PreviewCriterion]) -> list[dict]:
    """Đưa tiêu chí chấm-thử về ĐÚNG shape mà `provider.score()` nhận trên đường production.

    Sau khi lột 3 field kỳ vọng, dict còn lại là `{criterionId, name, description, maxScore,
    weight, levels}` — trùng khít thứ `ScoringCriteriaBuilder` (C#) gửi qua RabbitMQ cho ứng viên
    thật. Đây là chỗ dễ trôi nhất của cả tính năng: lệch một field ở đây thì HR kiểm chứng thước
    A trong khi ứng viên bị chấm thước B, và KHÔNG có triệu chứng nào.
    """
    out: list[dict] = []
    for c in criteria:
        item = c.model_dump()
        for field in _PREVIEW_EXPECTED_FIELDS:
            item.pop(field, None)
        out.append(item)
    return out


@router.post("/score-preview", response_model=ScorePreviewResponse)
async def score_preview(req: ScorePreviewRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    """E9b — CHẤM THỬ: AI viết 3 bài mẫu cho 1 câu hỏi rồi chấm THẬT cả 3.

    Employer hiện không có cách nào biết AI sẽ chấm ứng viên của mình thế nào — họ khai tiêu chí
    rồi phát link, phần còn lại là hộp đen. Endpoint này mở hộp đen đó ra.

    🔴 BA RÀNG BUỘC LÀM NÊN GIÁ TRỊ CỦA NÓ — sửa cái nào cũng làm cả tính năng thành trang trí:

    1. **Đi qua ĐÚNG `build_scoring_prompt` + `provider.score`** mà ứng viên thật đi qua, không
       một dòng khác. Có golden test khoá lại. Chấm thử qua một prompt khác = HR kiểm chứng thước
       A, ứng viên bị chấm thước B, và không có triệu chứng nào.
    2. **Mỗi bài MỘT lời gọi riêng** — bài không biết mình thuộc band nào và không thấy hai bài
       kia. Gộp 3 bài vào một prompt biến bài toán thành XẾP HẠNG ⇒ thứ tự yếu-khá-giỏi ra đúng
       bất kể thước đo tốt hay không ⇒ tự bịt mắt đúng chỗ cần nhìn.
    3. **`delivery=None`** — bài mẫu là văn bản, không có audio, nên không có số đo cách nói (F11)
       và prompt sẽ nói thẳng "chưa đo được, TUYỆT ĐỐI không bịa số". CỐ Ý **không** loại tiêu chí
       trôi chảy khỏi danh sách: bỏ một tiêu chí là đổi `rubric_block` ⇒ đổi điểm các tiêu chí còn
       lại, đổi mẫu số `overall = sumPct / scoredCriteriaCount` (INT-10), và nhận diện nó bằng
       khớp tên ("trôi chảy|fluency") là heuristic sẽ bắn nhầm — tên do HR gõ và có hai ngôn ngữ.
    """
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.question or not req.question.strip():
        raise HTTPException(status_code=400, detail="question không được rỗng")
    if not req.criteria:
        raise HTTPException(status_code=400, detail="criteria không được rỗng")

    for c in req.criteria:
        scores = {lv.score for lv in c.levels}
        # Thiếu mốc ⇒ `score()` rơi về dải mặc định 0..maxScore ⇒ bài kiểm chứng sẽ xác nhận một
        # thước đo KHÁC thước đo HR vừa soạn. Fail-loud, đừng để nó chạy.
        if len(scores) < 2:
            raise HTTPException(
                status_code=400,
                detail=f"Tiêu chí {c.criterionId}: cần ít nhất 2 mốc điểm khác nhau để chấm thử.")
        # Mức kỳ vọng không nằm trong thang ⇒ báo cáo expected-vs-actual so hai thứ khác đơn vị,
        # mà nó lại hiện ra như một con số đáng tin.
        for field in _PREVIEW_EXPECTED_FIELDS:
            expected = getattr(c, field)
            if expected not in scores:
                raise HTTPException(
                    status_code=400,
                    detail=f"Tiêu chí {c.criterionId}: {field}={expected} không phải một mốc "
                           f"trong thang {sorted(scores)}.")

    try:
        generated = await provider.generate_preview_answers(
            req.question, [c.model_dump() for c in req.criteria],
            req.targetWordCount, req.sampleAnswer,
            language=req.language, seniority=req.seniority)

        scoring_criteria = build_preview_scoring_criteria(req.criteria)

        pending = [(a.band, a.text, a.word_count) for a in generated.answers]
        if req.customAnswer and req.customAnswer.strip():
            # Bài thứ 4 do HR tự dán — bài DUY NHẤT trong bộ này không do chính bộ chấm viết ra,
            # nên là đối chứng duy nhất không dính self-scoring bias.
            custom = req.customAnswer.strip()
            pending.append(("Custom", custom, gemini_module.preview_word_count(custom)))

        # Song song: 3-4 lượt chấm tuần tự sẽ kéo request đồng bộ này lên gấp mấy lần, mà HR đang
        # ngồi chờ. Mỗi lượt vẫn là một lời gọi ĐỘC LẬP — xem ràng buộc 2 ở docstring.
        outcomes = await asyncio.gather(*[
            provider.score(
                question=req.question,
                transcript=text,
                job_category=req.jobCategory,
                criteria=scoring_criteria,
                temperature=0.0,          # ĐÚNG attempt-1 của production (E10)
                delivery=None,            # bài là văn bản → không có số đo cách nói (F11)
                language=req.language,
                sample_answer=req.sampleAnswer,
            )
            for _, text, _ in pending
        ])
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi chấm thử: {ex}")

    samples = [
        PreviewSample(
            band=band, answerText=text, wordCount=words,
            scores=[PreviewCriterionScore(**s) for s in outcome.scores],
        )
        for (band, text, words), outcome in zip(pending, outcomes)
    ]

    return ScorePreviewResponse(
        samples=samples,
        # BK23 — con dấu của bộ mảnh prompt ĐÃ dựng nên chính những lượt chấm này (`score()` chụp
        # tại chỗ). Thiếu nó, HR so hai lần chấm thử rồi quy mọi thay đổi cho việc mình sửa mốc,
        # trong khi admin có thể vừa sửa prompt F21 ở giữa.
        promptVersion=outcomes[0].prompt_version if outcomes else None,
        lengthParityWarning=generated.length_parity_warning,
    )


@router.post("/analyze-cv", response_model=AnalyzeCvResponse, response_model_exclude_none=True)
async def analyze_cv(req: AnalyzeCvRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.cvText or not req.cvText.strip():
        raise HTTPException(status_code=400, detail="cvText không được rỗng")
    try:
        requirements = None
        if req.mustHave is not None or req.niceToHave is not None:
            requirements = [
                {**item, "priority": "MustHave"} for item in (req.mustHave or [])
            ] + [
                {**item, "priority": "NiceToHave"} for item in (req.niceToHave or [])
            ]
        if requirements is None:
            # Giữ call-shape legacy để provider/test double cũ không bị phá.
            result = await _call_with_language(
                req.language, provider.analyze_cv,
                req.cvText, req.jdText, req.jobCategory)
        else:
            result = await _call_with_language(
                req.language, provider.analyze_cv,
                req.cvText, req.jdText, req.jobCategory,
                requirements=requirements)
        # REQUIREMENT mode có một nguồn sự thật duy nhất; không phát lại jdMatch holistic.
        jd_match = (
            JdMatch(**result["jdMatch"])
            if requirements is None and result.get("jdMatch") else None
        )
        return AnalyzeCvResponse(
            summary=result["summary"],
            strengths=result["strengths"],
            weaknesses=result["weaknesses"],
            suggestions=result["suggestions"],
            jdMatch=jd_match,
            requirementMatches=(
                [CvRequirementMatch(**m) for m in result["requirementMatches"]]
                if result.get("requirementMatches") is not None else None
            ),
            cvSections=(
                [CvSectionAnchor(**s) for s in result["cvSections"]]
                if result.get("cvSections") is not None else None
            ),
            citations=(
                [GroundingChunk(**c) for c in result["citations"]]
                if result.get("citations") is not None else None
            ),
        )
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi phân tích CV: {ex}")


@router.post("/suggest-job-needs", response_model=SuggestJobNeedsResponse)
async def suggest_job_needs(req: SuggestJobNeedsRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    """Bước 1 của HR technical screener — CampaignService gọi lúc publish campaign.

    Chỉ đọc JD nên chạy MỘT LẦN cho cả campaign; mọi ứng viên sau đó được đối chiếu với đúng
    bộ nhu cầu này (đường sàng từng CV không đi qua HTTP mà gọi thẳng provider trong worker).

    ``needId`` không sinh ở đây — CampaignService cấp, vì nó mới là nơi lưu và nơi HR sửa.
    """
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.jdText or not req.jdText.strip():
        raise HTTPException(status_code=400, detail="jdText không được rỗng")
    try:
        needs = await _call_with_language(req.language, provider.suggest_job_needs,
                                          req.jdText, req.jobCategory)
        return SuggestJobNeedsResponse(
            needs=[JobNeed(needId="", category=n["category"], text=n["text"]) for n in needs])
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi đề xuất nhu cầu công việc: {ex}")


@router.post("/analyze-repo", response_model=AnalyzeRepoResponse, response_model_exclude_none=True)
async def analyze_repo(req: AnalyzeRepoRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.repoDigest or not req.repoDigest.strip():
        raise HTTPException(status_code=400, detail="repoDigest không được rỗng")
    try:
        result = await _call_with_language(req.language, provider.analyze_repo, req.repoDigest, req.jdText, req.jobCategory)
        return AnalyzeRepoResponse(**result)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi phân tích repository: {ex}")


@router.post("/generate-roadmap", response_model=GenerateRoadmapResponse)
async def generate_roadmap(req: GenerateRoadmapRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.level or not req.level.strip():
        raise HTTPException(status_code=400, detail="level không được rỗng")
    try:
        weaknesses = [w.model_dump() for w in req.weaknesses] if req.weaknesses else None
        grounding = [g.model_dump() for g in req.grounding] if req.grounding else None
        milestones = await _call_with_language(req.language, provider.generate_roadmap,
            req.jobCategory, req.level, weaknesses, req.cvText,
            focus=req.focus,
            cv_analysis_summary=req.cvAnalysisSummary,
            prior_roadmap_summary=req.priorRoadmapSummary,
            grounding=grounding,
        )
        return GenerateRoadmapResponse(
            milestones=[
                RoadmapMilestone(
                    title=m["title"],
                    focusCriteria=m["focusCriteria"],
                    lessons=[RoadmapLesson(**l) for l in m["lessons"]],
                )
                for m in milestones
            ]
        )
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh roadmap: {ex}")


@router.post("/generate-lesson-theory", response_model=GenerateLessonTheoryResponse,
             response_model_exclude_none=True)
async def generate_lesson_theory(req: GenerateLessonTheoryRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.lessonTitle or not req.lessonTitle.strip():
        raise HTTPException(status_code=400, detail="lessonTitle không được rỗng")
    try:
        # RAG grounding (Contract 2) — vắng → ungrounded (cited_chunk_ids = None → field ẩn).
        grounding = [g.model_dump() for g in req.grounding] if req.grounding else None
        theory, resources, cited = await _call_with_language(req.language, provider.generate_lesson_theory,
            req.jobCategory, req.level, req.lessonTitle, req.focusCriteria,
            req.weaknesses, grounding)
        # F15 — resources đã sanitize ở provider (allowlist tên miền); rỗng là hợp lệ.
        # cited=None (ungrounded) → response_model_exclude_none bỏ field → shape cũ giữ nguyên.
        return GenerateLessonTheoryResponse(
            theoryMarkdown=theory, resources=resources, citedChunkIds=cited)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh lý thuyết lesson: {ex}")


@router.post("/summarize-roadmap", response_model=SummarizeRoadmapResponse)
async def summarize_roadmap(req: SummarizeRoadmapRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    try:
        progress = [c.model_dump() for c in req.criteriaProgress]
        result = await _call_with_language(req.language, provider.summarize_roadmap, req.jobCategory, req.level, progress)
        return SummarizeRoadmapResponse(**result)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi tổng kết roadmap: {ex}")


@router.post("/summarize-session", response_model=SummarizeSessionResponse)
async def summarize_session(req: SummarizeSessionRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    try:
        criteria = [c.model_dump() for c in req.criteriaScores]
        result = await _call_with_language(req.language, provider.summarize_session, req.jobCategory, req.overallScore, criteria)
        return SummarizeSessionResponse(**result)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi tổng kết buổi luyện: {ex}")


@router.post("/face-verify", response_model=FaceVerifyResponse)
async def face_verify(
    req: FaceVerifyRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token"),
):
    """SEC-2/3 — đối chiếu ảnh live ↔ ảnh tham chiếu + đếm mặt trên ảnh live.

    Kéo 2 ảnh từ S3 theo key → detect+embed (thread, nặng CPU) → dựng cờ (theo ảnh LIVE):
      0 mặt → no_face · >1 mặt → multiple_faces · 1 mặt & score < threshold → face_mismatch.
    Riêng ca ảnh MỐC không đọc được mặt → identity_unverified (lỗi ở ảnh mốc, không phải ứng viên).
    Mọi tín hiệu = CỜ cho HR (SEC-4), KHÔNG tự chặn/hủy bài.

    Gate X-Internal-Token, fail-closed (GEN-7): endpoint máy-máy — CampaignService gọi, kéo
    ảnh S3 + chạy model CPU. Phải bảo vệ như /decide-next và /tts (trước đây chỉ dựa cô lập mạng)."""
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.referenceImageKey or not req.referenceImageKey.strip():
        raise HTTPException(status_code=400, detail="referenceImageKey không được rỗng")
    if not req.liveImageKey or not req.liveImageKey.strip():
        raise HTTPException(status_code=400, detail="liveImageKey không được rỗng")

    threshold = req.threshold if req.threshold is not None else settings.face_match_threshold

    try:
        # Tải ảnh (S3 blocking) + so khớp (model nặng CPU) → thread, không block event loop.
        ref_bytes = await asyncio.to_thread(storage.get_object_bytes, req.referenceImageKey)
        live_bytes = await asyncio.to_thread(storage.get_object_bytes, req.liveImageKey)
        result = await asyncio.to_thread(
            face_verifier.compare, ref_bytes, live_bytes)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi đối chiếu khuôn mặt: {ex}")

    score, face_count = result.score, result.face_count

    signals: list[str] = []
    if face_count == 0:
        signals.append("no_face")
        match = False
    elif face_count > 1:
        signals.append("multiple_faces")
        match = False
    elif result.reference_face_count != 1:
        # Ảnh MỐC không dùng được (0 mặt vì enroll trúng khung đen/tối, hoặc nhiều mặt) — đây
        # KHÔNG phải lỗi của ứng viên đang ngồi trước camera, nên tuyệt đối không gắn
        # `face_mismatch` ("không đúng người"): cờ đó là cờ nặng nhất, HR đọc xong là loại.
        # `identity_unverified` = "chưa xác minh được danh tính", đúng nghĩa và ĐÃ có sẵn trong
        # IdentitySignals của CampaignService lẫn nhánh "chưa enroll" của FaceVerifyController.
        signals.append("identity_unverified")
        match = False
    else:
        match = score >= threshold
        if not match:
            signals.append("face_mismatch")

    return FaceVerifyResponse(
        faceCount=face_count,
        match=match,
        score=round(float(score), 4),
        signals=signals,
    )


@router.post("/transcribe")
async def transcribe(file: UploadFile = File(...), language: str = "vi",
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    # Lưu tạm file để faster-whisper đọc (nó nhận path)
    suffix = os.path.splitext(file.filename or "")[1] or ".tmp"
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
            tmp.write(await file.read())
            tmp_path = tmp.name

        # transcribe nặng CPU → chạy trong thread, không block event loop
        result = await asyncio.to_thread(transcriber.transcribe_detailed, tmp_path, language)

        # F11 — kèm chỉ số cách nói (None = không đo được). `text` giữ nguyên → client cũ không vỡ.
        # `transcriptEngine` = engine đã thật sự chép (có thể là bản dự phòng cục bộ nếu nhà cung
        # cấp từ xa hỏng) — endpoint này là chỗ soi tay, không có con dấu thì không soi được gì.
        return {
            "text": result.text,
            "deliveryMetrics": result.metrics.to_dict() if result.metrics else None,
            "transcriptEngine": result.engine,
        }
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi transcribe: {ex}")
    finally:
        if "tmp_path" in dir() and os.path.exists(tmp_path):
            os.remove(tmp_path)


@router.post("/decide-next", response_model=DecideNextResponse)
async def decide_next(
    req: DecideNextRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token"),
):
    """Phỏng vấn THÍCH ỨNG — transcribe (nếu có audio) + quyết định hành động kế tiếp.

    InterviewService (chủ state) gọi sau mỗi câu trả lời; AIService stateless (GEN-4).
    Transcript trả về là NGUỒN DUY NHẤT — Interview lưu lên answer + đẩy vào ScoringJob
    để worker khỏi transcribe lại (bỏ N lần Whisper của self-consistency E10)."""
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")

    # Nguồn transcript: ưu tiên audioObjectKey (transcribe tại đây) → answerText (fallback test).
    transcript: str | None = None
    # F11 — chỉ số cách nói CHỈ đo được ở nhánh có audio thật; nhánh answerText (không có audio)
    # để None, và .NET/prompt hiểu None = "chưa đo được" chứ không phải "đo ra 0".
    metrics = None
    # Con dấu engine — cùng lý do: nhánh answerText không chép lời nên không có engine nào.
    engine: str | None = None
    if req.audioObjectKey and req.audioObjectKey.strip():
        tmp_path = None
        try:
            data = await asyncio.to_thread(storage.get_object_bytes, req.audioObjectKey)
            suffix = os.path.splitext(req.audioObjectKey)[1] or ".webm"
            with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
                tmp.write(data)
                tmp_path = tmp.name
            result = await asyncio.to_thread(
                transcriber.transcribe_detailed, tmp_path, req.language)
            transcript, metrics, engine = result.text, result.metrics, result.engine
            reject_reason = result.reject_reason
        except Exception as ex:
            raise HTTPException(status_code=502, detail=f"Lỗi transcribe: {ex}")
        finally:
            if tmp_path and os.path.exists(tmp_path):
                os.remove(tmp_path)

        # Bản chép bị TỪ CHỐI → KHÔNG hỏi Gemini câu kế: không có gì để đào sâu, và hỏi nó về một
        # transcript rỗng chỉ tốn tiền để nhận một câu hỏi bịa. Trả thẳng cho .NET quyết (nó đánh
        # answer `Skipped` và không publish job chấm).
        #
        # `action="end"` = "lượt này không sinh câu kế", KHÔNG phải "buổi đã xong": .NET bản mới đọc
        # `rejectReason` và thoát trước khi nhìn tới action, còn .NET bản CŨ (cửa sổ deploy lệch nhịp)
        # suy `interviewComplete` theo số câu CHƯA trả lời chứ không cứng theo action — nên ca xấu
        # nhất cũng chỉ là mời nộp bài sớm, không tạo ra điểm giả.
        if reject_reason is not None:
            return DecideNextResponse(
                action="end",
                nextQuestion=None,
                transcript=None,
                reason=f"Bản chép bị từ chối: {reject_reason}",
                deliveryMetrics=None,
                transcriptEngine=engine,
                rejectReason=reject_reason,
            )
    elif req.answerText and req.answerText.strip():
        transcript = req.answerText.strip()
    else:
        raise HTTPException(status_code=400, detail="Thiếu audioObjectKey hoặc answerText")

    try:
        decision = await _call_with_language(req.language, provider.decide_next,
            job_category=req.jobCategory,
            current_question=req.currentQuestion,
            transcript=transcript or "",
            history=[t.model_dump() for t in req.history],
            asked_count=req.askedCount,
            follow_up_count=req.followUpCount,
            max_questions=req.maxQuestions,
            max_follow_ups=req.maxFollowUps,
            criteria=[c.model_dump() for c in req.criteria],
            # INT-17b — ngữ cảnh chuỗi đào sâu (maxDepth = 0 ⇒ giữ nguyên hành vi cũ).
            root_question=req.rootQuestion,
            current_depth=req.currentDepth,
            max_depth=req.maxDepth,
            other_topics=req.otherTopics,
            seniority=req.seniority,
            current_evidence_state=[e.model_dump() for e in req.currentEvidenceState],
        )
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi quyết định câu hỏi kế: {ex}")

    return DecideNextResponse(
        action=decision["action"],
        nextQuestion=decision.get("nextQuestion"),
        transcript=transcript,
        reason=decision.get("reason"),
        # F11 — trả kèm chỉ số để Interview lưu lên answer + đẩy vào ScoringJob: buổi adaptive
        # bỏ Whisper ở worker nên ĐÂY là lần đo DUY NHẤT của câu trả lời này.
        deliveryMetrics=DeliveryMetrics(**metrics.to_dict()) if metrics else None,
        # Cùng lý do với deliveryMetrics: buổi adaptive chép lời ĐÚNG MỘT LẦN tại đây, nên nếu
        # con dấu không đi ra ở đây thì nó không còn cơ hội nào khác.
        transcriptEngine=engine,
        targetCriterionId=decision.get("targetCriterionId"),
        evidenceFound=decision.get("evidenceFound"),
        missingEvidence=decision.get("missingEvidence"),
        newEvidenceState=decision.get("newEvidenceState"),
    )


@router.post("/tts")
async def text_to_speech(
    req: TtsRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token"),
):
    """Đọc câu hỏi thành tiếng → trả BYTES mp3 (audio/mpeg).

    Cache theo NỘI DUNG trên S3: key = tts/{sha256(voice+text)}.mp3 (xem app/tts.py).
    Hit → trả thẳng, KHÔNG gọi vendor (đây là chỗ tiết kiệm tiền — có test khoá lại).
    Miss → tổng hợp → encode mp3 → ghi S3 → trả.

    KHÔNG trừ credit: đây là trợ năng đọc câu hỏi, không phải lượt phỏng vấn được AI
    chấm (PAY-1). GEN-4 vẫn giữ: AIService chỉ ghi object storage, không ghi DB.
    Gate X-Internal-Token, fail-closed (máy-máy, InterviewService gọi — GEN-7)."""
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")

    text = (req.text or "").strip()
    if not text:
        raise HTTPException(status_code=400, detail="text không được rỗng")

    voice = (req.voice or settings.tts_voice).strip() or settings.tts_voice
    language_code = settings.tts_language_code_en if req.language == "en" else settings.tts_language_code
    key = tts.cache_key(text, voice, language_code)

    # ── 1. Thử cache ───────────────────────────────────────────────────────────
    # S3 blocking → thread, không chẹn event loop (idiom /face-verify).
    try:
        cached = await asyncio.to_thread(storage.try_get_object_bytes, key)
    except Exception as ex:
        # S3 hỏng THẬT (không phải "chưa có"). Không nuốt: nuốt = mọi request thành
        # cache-miss và gọi vendor mãi mãi (đốt tiền âm thầm).
        raise HTTPException(status_code=502, detail=f"Lỗi đọc cache TTS: {ex}")

    if cached:
        return Response(content=cached, media_type=tts.MP3_CONTENT_TYPE,
                        headers={"X-Tts-Cache": "hit"})

    # ── 2. Miss → gọi vendor + encode ──────────────────────────────────────────
    try:
        pcm, mime_type = await provider.synthesize_speech(
            text, voice, language_code)
        # Gemini TTS trả PCM thô, không phải mp3 → encode (ffmpeg, xem app/audio.py).
        mp3 = await asyncio.to_thread(
            audio.pcm_to_mp3, pcm, audio.parse_pcm_rate(mime_type))
    except Exception as ex:
        # Vendor chết/quá tải/encode lỗi → 502 sạch. FE degrade về chỉ hiện chữ;
        # luồng phỏng vấn KHÔNG bị chặn.
        raise HTTPException(status_code=502, detail=f"Lỗi tổng hợp giọng đọc: {ex}")

    # ── 3. Ghi cache (best-effort) ─────────────────────────────────────────────
    # Ghi hỏng KHÔNG được làm hỏng request: audio đã có trong tay, người dùng nghe được.
    # Nhưng phải QUAN SÁT ĐƯỢC — nếu ghi luôn hỏng thì mọi request đều gọi vendor, nên
    # đánh dấu "miss-nostore" để log/monitor thấy, thay vì im lặng.
    cache_state = "miss"
    try:
        await asyncio.to_thread(
            storage.put_object_bytes, key, mp3, tts.MP3_CONTENT_TYPE)
    except Exception:
        cache_state = "miss-nostore"

    return Response(content=mp3, media_type=tts.MP3_CONTENT_TYPE,
                    headers={"X-Tts-Cache": cache_state})


@router.post("/embed", response_model=EmbedResponse)
async def embed(
    req: EmbedRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token"),
):
    """RAG grounding (Contract 1) — sinh embedding cho batch text.

    InterviewService (chủ kho tri thức) gọi lúc INGEST (task_type=RETRIEVAL_DOCUMENT → upsert
    Qdrant) và lúc TRUY HỒI (RETRIEVAL_QUERY → vector search). AIService stateless (GEN-4): chỉ
    sinh vector, KHÔNG ghi kho nào.

    Gate X-Internal-Token, fail-closed (máy-máy — GEN-7, mẫu /decide-next /tts /face-verify).
    Lỗi provider (Gemini quá tải/model lạ) → 502."""
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    try:
        vectors = await provider.embed(req.texts, req.taskType)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh embedding: {ex}")
    return EmbedResponse(vectors=vectors, dim=settings.embed_dim, model=settings.embed_model)


# Kích hoạt toàn bộ route /api/v1 — đăng ký SAU khi mọi @router đã khai báo.
app.include_router(router)
