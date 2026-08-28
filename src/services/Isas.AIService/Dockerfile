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

# AC1/B5 — model nhúng giọng nói cho detector `multi_voice` (WeSpeaker CAM++, VoxCeleb, 28 MB).
# TẢI LÚC BUILD, KHÔNG lúc chạy: một lượt tải 28 MB nằm giữa đường chấm là thêm một cách hỏng mới
# cho ĐƯỜNG TIỀN (PAY-13), và nó sẽ hỏng đúng lúc mạng đang tệ — tức đúng lúc mọi thứ khác cũng
# đang hỏng. Ghim cả URL LẪN sha256: thiếu sha256 thì "cùng một commit" vẫn ra hai image khác nhau
# nếu upstream thay file, mà đây là model quyết định một cáo buộc gian lận.
# Kích thước image tăng ~28 MB (594 MB → ~622 MB). KHÔNG kéo torch (chỉ onnxruntime + numpy, cả
# hai đã có sẵn cho insightface); fbank Kaldi tự cài bằng numpy trong app/fbank.py.
#
# ⚠ CỐ Ý KHÔNG dùng `ADD --checksum=...` (gọn hơn nhiều): cú pháp đó cần frontend BuildKit ≥1.5,
# mà Dockerfile này KHÔNG khai `# syntax=`, nên nó phụ thuộc phiên bản BuildKit dựng sẵn của máy
# build. Không kiểm chứng được ở đây (máy dev không chạy docker daemon) và hỏng thì gãy nguyên
# đường deploy, nên chọn cách chạy được với MỌI phiên bản Docker. `python` đã là base image, không
# cần curl/wget (python:slim không có sẵn cả hai).
# (Heredoc `<<'PY'` cũng cần frontend ≥1.4 nên CỐ Ý không dùng — `python -c` chạy ở mọi nơi.)
# Checksum sai ⇒ SystemExit ⇒ build ĐỎ. Cố ý KHÔNG "tải lại cho chắc": sai checksum nghĩa là file
# không phải thứ đã được hiệu chuẩn, mà model này quyết định một cáo buộc gian lận.
ARG SPEAKER_MODEL_URL=https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_CAM++.onnx
ARG SPEAKER_MODEL_SHA256=c46fad10b5f81e1aa4a60c162714208577093655076c5450f8c469e522ec54ef
RUN python -c "import hashlib,os,sys,urllib.request; \
url=os.environ['SPEAKER_MODEL_URL']; want=os.environ['SPEAKER_MODEL_SHA256']; \
dst='/app/models/speaker-embedding.onnx'; \
os.makedirs('/app/models', exist_ok=True); \
urllib.request.urlretrieve(url, dst); \
got=hashlib.sha256(open(dst,'rb').read()).hexdigest(); \
sys.exit('sha256 model KHONG khop: mong %s, nhan %s' % (want, got)) if got != want else None; \
print('model nhung giong noi OK (%d bytes, sha256 khop)' % os.path.getsize(dst))" \
    && ls -l /app/models/speaker-embedding.onnx

COPY app ./app

EXPOSE 8000

# Mặc định = API sinh câu hỏi. Worker chấm điểm: override `command: python -m app.worker` trong compose.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
