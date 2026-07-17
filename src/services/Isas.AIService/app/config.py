from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    gemini_api_key: str
    gemini_model: str = "gemini-2.5-flash"
    question_count: int = 5

    # ── SCORING RETRY (AI3) ──────────────────────────────────────
    # score() raise ValueError khi LLM trả output không parse/không hợp lệ. Lỗi
    # parse thường CHỢP NHOÁNG (JSON cụt, thỉnh thoảng malformed) → thử lại vài
    # lần trước khi bó tay báo answer Failed. 3 = 1 lần đầu + 2 lần retry.
    score_max_attempts: int = 3

    # Whisper
    whisper_model: str = "large-v3"
    whisper_device: str = "cpu"
    whisper_compute_type: str = "int8"

    # ── FACE VERIFY (SEC-2/3) ────────────────────────────────────
    # buffalo_l = pack insightface mặc định (detect + ArcFace embed). CPU-only.
    face_model_name: str = "buffalo_l"
    # Ngưỡng cosine-similarity coi là KHỚP mặt (≥ → match). Caller có thể override/request.
    face_match_threshold: float = 0.4

    # ── RABBITMQ CONFIG ──────────────────────────────────────────
    rabbitmq_url: str = "amqp://guest:guest@localhost/"
    queue_name: str = "scoring_pipeline_queue"   # TRÙNG RabbitMQ:QueueName .NET

    # ── DEAD-LETTER (AI2) ────────────────────────────────────────
    # Message bị nack(requeue=False) → broker đẩy sang DLX → DLQ thay vì XOÁ IM LẶNG.
    # 3 tên này khai vào `arguments` của queue chính; PHẢI TRÙNG y hệt args khai ở
    # .NET ScoringJobPublisher.cs — lệch 1 ký tự → RabbitMQ 406 PRECONDITION_FAILED
    # khi bên còn lại redeclare cùng queue với arguments khác.
    dlx_name: str = "scoring_pipeline_dlx"                 # dead-letter exchange (direct)
    dead_queue_name: str = "scoring_pipeline_dead_queue"  # nơi giữ message chết
    dead_routing_key: str = "scoring_dead"                # DLX → DLQ binding key

    # ── S3 / SEAWEEDFS CONFIG ────────────────────────────────────
    s3_endpoint: str = "http://localhost:8333"
    s3_access_key: str = "your-access-key"
    s3_secret_key: str = "your-secret-key"
    s3_bucket: str = "isas-files"

    # ── CALLBACK VỀ .NET ─────────────────────────────────────────
    dotnet_callback_base: str = "http://localhost:5246"  # cổng .NET API
    internal_token: str = "change-me"                    # trùng Internal:Token .NET


settings = Settings()