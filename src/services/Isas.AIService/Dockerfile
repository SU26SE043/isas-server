# AIService (FastAPI sinh câu hỏi + worker chấm điểm).
# 1 image dùng chung: api chạy uvicorn (CMD mặc định), worker override CMD = python -m app.worker.
# python:3.12 vì ctranslate2/faster-whisper chưa có wheel cho 3.14.
FROM python:3.12-slim

WORKDIR /app

# ffmpeg cho faster-whisper decode webm/opus; libgl1 + libglib2.0-0 cho opencv (insightface).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg libgl1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

# `-c requirements.lock.txt` khoá phiên bản MỌI gói (kể cả bắc cầu: ctranslate2, onnxruntime,
# tokenizers…) về đúng bộ đang chạy production. Không có nó, hai lần build cách nhau vài ngày
# từ CÙNG một commit ra hai image khác nhau. Xem header của file lock để biết cách nâng cấp.
COPY requirements.txt requirements.lock.txt ./
RUN pip install --no-cache-dir -r requirements.txt -c requirements.lock.txt

# (Tùy chọn) Pre-download model insightface lúc build để request đầu không phải tải ~300MB.
# Bỏ comment nếu môi trường build có mạng tới kho model:
# RUN python -c "from insightface.app import FaceAnalysis; \
#     FaceAnalysis(name='buffalo_l', providers=['CPUExecutionProvider']).prepare(ctx_id=-1)"

COPY app ./app

EXPOSE 8000

# Mặc định = API sinh câu hỏi. Worker chấm điểm: override `command: python -m app.worker` trong compose.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
