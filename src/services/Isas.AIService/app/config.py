from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    gemini_api_key: str
    gemini_model: str = "gemini-2.5-flash"
    question_count: int = 5

    # ── RAG GROUNDING: EMBEDDING (Phase 1) ───────────────────────────
    # gemini-embedding-001 = model đa ngôn ngữ (100+, có tiếng Việt) mạnh cross-lingual
    # VN↔EN → query tiếng Việt tìm thẳng chunk tiếng Anh, KHÔNG cần dịch. Matryoshka:
    # output_dimensionality=768 khớp collection Qdrant `knowledge` (768, Cosine). Endpoint
    # /embed stateless (GEN-4 — chỉ sinh vector, KHÔNG ghi kho nào; InterviewService quản Qdrant).
    embed_model: str = "gemini-embedding-001"
    embed_dim: int = 768

    # ── SCORING RETRY (AI3) ──────────────────────────────────────
    # score() raise ValueError khi LLM trả output không parse/không hợp lệ. Lỗi
    # parse thường CHỢP NHOÁNG (JSON cụt, thỉnh thoảng malformed) → thử lại vài
    # lần trước khi bó tay báo answer Failed. 3 = 1 lần đầu + 2 lần retry.
    score_max_attempts: int = 3

    # ── TTS: ĐỌC CÂU HỎI THÀNH TIẾNG ────────────────────────────
    # Dùng LẠI gemini_api_key ở trên → KHÔNG phải cấp credential mới.
    # Model TTS là model RIÊNG (model chat thường không nhận response_modalities=["AUDIO"]).
    tts_model: str = "gemini-2.5-flash-preview-tts"
    # Giọng dựng sẵn. Đổi voice ⇒ đổi cache key ⇒ audio cũ tự hết hiệu lực (khỏi purge tay).
    tts_voice: str = "Kore"
    # Gemini TTS hỗ trợ tiếng Việt (vi-VN). Đây là hằng phía SERVER — client KHÔNG truyền vào
    # (nếu sau này cho client chọn ngôn ngữ thì PHẢI đưa nó vào cache key, xem app/tts.py).
    tts_language_code: str = "vi-VN"
    # Tiền tố key cache S3 — key nội-dung-định-danh: tts/{sha256(voice+text)}.mp3
    tts_cache_prefix: str = "tts/"

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

    # ── F22: ĐO TOKEN/CHI PHÍ (FR18) ─────────────────────────────
    # GEN-4 cấm AIService ghi DB → số liệu được ĐẨY qua callback nội bộ về
    # PaymentService (chỗ đã giữ doanh thu F19; chi phí AI chỉ có nghĩa khi đọc
    # cạnh doanh thu). Xem app/usage.py cho các phương án đã loại.
    usage_metering_enabled: bool = True
    # Base URL PaymentService (KHÔNG qua gateway — GEN-1). Để RỖNG = chỉ ghi log,
    # không gọi mạng: đây là mặc định an toàn cho test/dev, và là kill-switch khi
    # sink có sự cố mà không phải deploy lại.
    usage_sink_base: str = ""
    # Ngắn có chủ đích: đây là lượt gọi PHỤ nằm sau một lượt LLM đã xong. Sink chậm
    # KHÔNG được kéo dài request của người dùng (/decide-next chạy đồng bộ trong
    # đường upload câu trả lời).
    usage_sink_timeout_seconds: float = 3.0

    # ── F21: PROMPT REGISTRY (FR17) ──────────────────────────────────────
    # GEN-4 cấm AIService ghi DB → mảnh prompt tuỳ biến nằm ở InterviewService, kéo về qua HTTP
    # nội bộ. Xem app/prompt_registry.py cho các phương án đã loại + 4 tầng fail-open.
    #
    # Base URL InterviewService (KHÔNG qua gateway — GEN-1). RỖNG = tắt hẳn, chạy thuần bản
    # hardcode trong prompts.py: mặc định an toàn cho test/dev, và là kill-switch khi registry
    # có sự cố mà không phải deploy lại.
    prompt_registry_base: str = ""
    # 60s: đủ ngắn để admin sửa xong thấy hiệu lực gần như ngay (FR17 "lần sinh kế dùng bản
    # mới"), đủ dài để không biến mỗi lượt gọi Gemini thành một lượt gọi mạng phụ.
    prompt_cache_ttl_seconds: float = 60.0
    # Ngắn: nạp prompt nằm TRƯỚC một lượt LLM trong cùng request. Registry chậm không được kéo
    # dài request của người dùng — hết giờ thì dùng bản đang có (tầng 3/4).
    prompt_fetch_timeout_seconds: float = 3.0


settings = Settings()