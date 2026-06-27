# AIService (FastAPI sinh câu hỏi + worker chấm điểm).
# 1 image dùng chung: api chạy uvicorn (CMD mặc định), worker override CMD = python -m app.worker.
# python:3.12 vì ctranslate2/faster-whisper chưa có wheel cho 3.14.
FROM python:3.12-slim

WORKDIR /app

# ffmpeg cho faster-whisper decode webm/opus
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app

EXPOSE 8000

# Mặc định = API sinh câu hỏi. Worker chấm điểm: override `command: python -m app.worker` trong compose.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
