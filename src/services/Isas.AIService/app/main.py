from fastapi import FastAPI, HTTPException, UploadFile, File, APIRouter
import os
import tempfile
import asyncio
from app.schemas import (
    GenerateQuestionsRequest, GenerateQuestionsResponse,
    SuggestCriteriaRequest, SuggestCriteriaResponse, CriterionItem,
    AnalyzeCvRequest, AnalyzeCvResponse, JdMatch,
)
from app.providers.gemini import GeminiProvider
from app.transcriber import Transcriber 

app = FastAPI(title="ISAS AI Service")
transcriber = Transcriber() 
provider = GeminiProvider()

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