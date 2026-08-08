from fastapi import FastAPI, HTTPException, UploadFile, File, APIRouter, Header, Response
import hmac
import os
import tempfile
import asyncio
from contextlib import asynccontextmanager
from app.schemas import (
    GenerateQuestionsRequest, GenerateQuestionsResponse, QuestionCitation,
    SuggestCriteriaRequest, SuggestCriteriaResponse, CriterionItem,
    AnalyzeCvRequest, AnalyzeCvResponse, AnalyzeRepoRequest, AnalyzeRepoResponse, JdMatch,
    CriterionMatch,
    GenerateRoadmapRequest, GenerateRoadmapResponse, RoadmapMilestone, RoadmapLesson,
    GenerateLessonTheoryRequest, GenerateLessonTheoryResponse,
    SummarizeRoadmapRequest, SummarizeRoadmapResponse,
    SummarizeSessionRequest, SummarizeSessionResponse,
    FaceVerifyRequest, FaceVerifyResponse,
    DecideNextRequest, DecideNextResponse, DeliveryMetrics,
    TtsRequest,
    EmbedRequest, EmbedResponse,
)
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

@router.post("/analyze-cv", response_model=AnalyzeCvResponse, response_model_exclude_none=True)
async def analyze_cv(req: AnalyzeCvRequest,
    x_internal_token: str | None = Header(default=None, alias="X-Internal-Token")):
    if not _valid_internal_token(x_internal_token):
        raise HTTPException(status_code=401, detail="Invalid internal token")
    if not req.cvText or not req.cvText.strip():
        raise HTTPException(status_code=400, detail="cvText không được rỗng")
    try:
        # C14 — criteria vắng ⇒ None (KHÔNG phải []): provider rẽ nhánh theo truthiness, và
        # response_model_exclude_none bỏ hẳn 5 field B2B ⇒ đường B2C giữ nguyên shape cũ.
        criteria = [c.model_dump() for c in req.criteria] if req.criteria else None
        result = await _call_with_language(req.language, provider.analyze_cv, req.cvText, req.jdText, req.jobCategory, criteria)
        jd_match = JdMatch(**result["jdMatch"]) if result.get("jdMatch") else None
        matches = ([CriterionMatch(**m) for m in result["criterionMatches"]]
                   if result.get("criterionMatches") else None)
        return AnalyzeCvResponse(
            summary=result["summary"],
            strengths=result["strengths"],
            weaknesses=result["weaknesses"],
            suggestions=result["suggestions"],
            jdMatch=jd_match,
            # BK28 — quên dòng này thì pydantic KHÔNG lỗi, field chỉ đơn giản không bao giờ ra wire
            # (cùng lớp bug `metricsVersion` rụng ở `DeliveryMetrics` 2026-08-05).
            fullName=result.get("fullName"),
            skills=result.get("skills"),
            yearsExperience=result.get("yearsExperience"),
            education=result.get("education"),
            criterionMatches=matches,
            overallMatchScore=result.get("overallMatchScore"),
        )
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi phân tích CV: {ex}")


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

    Kéo 2 ảnh từ S3 theo key → detect+embed (thread, nặng CPU) → dựng cờ:
      0 mặt → no_face · >1 mặt → multiple_faces · 1 mặt & score < threshold → face_mismatch.
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
        score, face_count = await asyncio.to_thread(
            face_verifier.compare, ref_bytes, live_bytes)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi đối chiếu khuôn mặt: {ex}")

    signals: list[str] = []
    if face_count == 0:
        signals.append("no_face")
        match = False
    elif face_count > 1:
        signals.append("multiple_faces")
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
        except Exception as ex:
            raise HTTPException(status_code=502, detail=f"Lỗi transcribe: {ex}")
        finally:
            if tmp_path and os.path.exists(tmp_path):
                os.remove(tmp_path)
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
