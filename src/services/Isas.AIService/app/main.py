from fastapi import FastAPI, HTTPException, UploadFile, File, APIRouter
import os
import tempfile
import asyncio
from app.schemas import (
    GenerateQuestionsRequest, GenerateQuestionsResponse,
    SuggestCriteriaRequest, SuggestCriteriaResponse, CriterionItem,
    AnalyzeCvRequest, AnalyzeCvResponse, JdMatch,
    GenerateRoadmapRequest, GenerateRoadmapResponse, RoadmapMilestone, RoadmapLesson,
    GenerateLessonTheoryRequest, GenerateLessonTheoryResponse,
    SummarizeRoadmapRequest, SummarizeRoadmapResponse,
    SummarizeSessionRequest, SummarizeSessionResponse,
    FaceVerifyRequest, FaceVerifyResponse,
)
from app.providers.gemini import GeminiProvider
from app.transcriber import Transcriber
from app.face_verify import FaceVerifier
from app.config import settings
from app import storage

app = FastAPI(title="ISAS AI Service")
transcriber = Transcriber()
provider = GeminiProvider()
face_verifier = FaceVerifier()

# 1. Định nghĩa Router
router = APIRouter(prefix="/api/v1")

@router.get("/health")
async def health():
    return {"status": "ok"}

@router.post("/generate-questions", response_model=GenerateQuestionsResponse)
async def generate_questions(req: GenerateQuestionsRequest):
    try:
        questions = await provider.generate(req.jobCategory, req.cvText, req.jdText)
        return GenerateQuestionsResponse(questions=questions)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh câu hỏi: {ex}")

@router.post("/suggest-criteria", response_model=SuggestCriteriaResponse)
async def suggest_criteria(req: SuggestCriteriaRequest):
    try:
        items = await provider.suggest_criteria(
            req.jobCategory, req.jdText, req.criteriaText, req.count)
        return SuggestCriteriaResponse(criteria=[CriterionItem(**c) for c in items])
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi đề xuất tiêu chí: {ex}")

@router.post("/analyze-cv", response_model=AnalyzeCvResponse, response_model_exclude_none=True)
async def analyze_cv(req: AnalyzeCvRequest):
    if not req.cvText or not req.cvText.strip():
        raise HTTPException(status_code=400, detail="cvText không được rỗng")
    try:
        result = await provider.analyze_cv(req.cvText, req.jdText, req.jobCategory)
        jd_match = JdMatch(**result["jdMatch"]) if result.get("jdMatch") else None
        return AnalyzeCvResponse(
            summary=result["summary"],
            strengths=result["strengths"],
            weaknesses=result["weaknesses"],
            suggestions=result["suggestions"],
            jdMatch=jd_match,
        )
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi phân tích CV: {ex}")


@router.post("/generate-roadmap", response_model=GenerateRoadmapResponse)
async def generate_roadmap(req: GenerateRoadmapRequest):
    if not req.level or not req.level.strip():
        raise HTTPException(status_code=400, detail="level không được rỗng")
    try:
        weaknesses = [w.model_dump() for w in req.weaknesses] if req.weaknesses else None
        milestones = await provider.generate_roadmap(
            req.jobCategory, req.level, weaknesses, req.cvText)
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


@router.post("/generate-lesson-theory", response_model=GenerateLessonTheoryResponse)
async def generate_lesson_theory(req: GenerateLessonTheoryRequest):
    if not req.lessonTitle or not req.lessonTitle.strip():
        raise HTTPException(status_code=400, detail="lessonTitle không được rỗng")
    try:
        theory = await provider.generate_lesson_theory(
            req.jobCategory, req.level, req.lessonTitle, req.focusCriteria, req.weaknesses)
        return GenerateLessonTheoryResponse(theoryMarkdown=theory)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh lý thuyết lesson: {ex}")


@router.post("/summarize-roadmap", response_model=SummarizeRoadmapResponse)
async def summarize_roadmap(req: SummarizeRoadmapRequest):
    try:
        progress = [c.model_dump() for c in req.criteriaProgress]
        result = await provider.summarize_roadmap(req.jobCategory, req.level, progress)
        return SummarizeRoadmapResponse(**result)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi tổng kết roadmap: {ex}")


@router.post("/summarize-session", response_model=SummarizeSessionResponse)
async def summarize_session(req: SummarizeSessionRequest):
    try:
        criteria = [c.model_dump() for c in req.criteriaScores]
        result = await provider.summarize_session(req.jobCategory, req.overallScore, criteria)
        return SummarizeSessionResponse(**result)
    except HTTPException:
        raise
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi tổng kết buổi luyện: {ex}")


@router.post("/face-verify", response_model=FaceVerifyResponse)
async def face_verify(req: FaceVerifyRequest):
    """SEC-2/3 — đối chiếu ảnh live ↔ ảnh tham chiếu + đếm mặt trên ảnh live.

    Kéo 2 ảnh từ S3 theo key → detect+embed (thread, nặng CPU) → dựng cờ:
      0 mặt → no_face · >1 mặt → multiple_faces · 1 mặt & score < threshold → face_mismatch.
    Mọi tín hiệu = CỜ cho HR (SEC-4), KHÔNG tự chặn/hủy bài."""
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
async def transcribe(file: UploadFile = File(...), language: str = "vi"):
    # Lưu tạm file để faster-whisper đọc (nó nhận path)
    suffix = os.path.splitext(file.filename or "")[1] or ".tmp"
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
            tmp.write(await file.read())
            tmp_path = tmp.name

        # transcribe nặng CPU → chạy trong thread, không block event loop
        text = await asyncio.to_thread(transcriber.transcribe, tmp_path, language)

        return {"text": text}
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi transcribe: {ex}")
    finally:
        if "tmp_path" in dir() and os.path.exists(tmp_path):
            os.remove(tmp_path)


# Kích hoạt toàn bộ route /api/v1 — đăng ký SAU khi mọi @router đã khai báo.
app.include_router(router)