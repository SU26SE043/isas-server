from fastapi import FastAPI, HTTPException, UploadFile, File
import os
import tempfile
import asyncio
from app.schemas import GenerateQuestionsRequest, GenerateQuestionsResponse
from app.providers.gemini import GeminiProvider
from app.transcriber import Transcriber 
app = FastAPI(title="ISAS AI Service")
transcriber = Transcriber() 
provider = GeminiProvider()


@app.get("/health")
async def health():
    return {"status": "ok"}


@app.post("/generate-questions", response_model=GenerateQuestionsResponse)
async def generate_questions(req: GenerateQuestionsRequest):
    try:
        questions = await provider.generate(req.jobCategory, req.cvText, req.jdText)
        return GenerateQuestionsResponse(questions=questions)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi sinh câu hỏi: {ex}")
    
@app.post("/transcribe")
async def transcribe(file: UploadFile = File(...), language: str = "vi"):
    # Lưu tạm file để faster-whisper đọc (nó nhận path)
    suffix = os.path.splitext(file.filename or "")[1] or ".tmp"
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
            tmp.write(await file.read())
            tmp_path = tmp.name

        # transcribe nặng CPU → chạy trong thread, không block event loop
        import asyncio
        text = await asyncio.to_thread(transcriber.transcribe, tmp_path, language)

        return {"text": text}
    except Exception as ex:
        raise HTTPException(status_code=502, detail=f"Lỗi transcribe: {ex}")
    finally:
        if "tmp_path" in dir() and os.path.exists(tmp_path):
            os.remove(tmp_path)