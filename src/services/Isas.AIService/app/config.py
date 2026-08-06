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

    # ── CHÉP LỜI QUA NHÀ CUNG CẤP TỪ XA ─────────────────────────
    # `"local"` (mặc định) = Whisper cục bộ như trước · `"whisper-1"` = OpenAI · `"gemini"`.
    # Từ xa hỏng (mạng/quota/bản chép có dấu hiệu hỏng) → TỰ ĐỘNG rơi về Whisper cục bộ; cục bộ
    # hỏng nốt thì giữ nguyên hành vi cũ (PermanentError → answer Failed). Xem
    # app/transcribe_providers.py để biết số đo và vì sao.
    #
    # ⚠ Mặc định TẮT theo đúng tiền lệ mọi rollout khác của repo (`GROUNDING_ENABLED`,
    # `TIERING_ENABLED`, `CV_SCREENING_ENABLED`): đây là năng lực MỚI, vừa tốn tiền theo lượt vừa
    # có hệ quả riêng tư (audio ứng viên rời khỏi hạ tầng của mình) — khác `delivery_metrics_source`
    # vốn là bản vá cho hành vi ĐÃ ĐO ĐƯỢC LÀ SAI nên phải bật sẵn.
    transcribe_provider: str = "local"
    # Credential RIÊNG của OpenAI (không dùng lại gemini_api_key được). Rỗng + provider
    # `"whisper-1"` ⇒ lượt gọi 401 → rơi về cục bộ (không sập, chỉ mất phần chất lượng).
    openai_api_key: str = ""
    # Model chép lời của OpenAI. Giá trị này CŨNG là con dấu `transcriptEngine` gửi về .NET, nên
    # đổi model ở đây thì số liệu lịch sử vẫn phân biệt được bản nào chép bằng gì.
    openai_transcribe_model: str = "whisper-1"
    # 60s: đo thật whisper-1 mất 23,9s cho 190s audio, gemini 29,9s — trần này chừa gấp đôi cho
    # mạng xấu mà vẫn nằm dưới timeout 90s của decider (`/decide-next` gọi ĐỒNG BỘ trong request
    # upload). Hết giờ = rơi về cục bộ, không phải lỗi.
    transcribe_timeout_seconds: float = 60.0
    # Rollout riêng cho payload gốc: false giữ WAV tái mã hoá tương thích tuyệt đối.
    transcribe_send_original: bool = False

    # ── NGÂN SÁCH "THINKING" CHO /decide-next ────────────────────
    # Gemini 2.5 mặc định bật suy luận ẩn (thinking) và tính tiền token đó THEO GIÁ OUTPUT.
    # Đo trên chính đường này: **934 thinking token cho 65 token output thật** — gấp 14 lần, và
    # đó chính là phần lớn độ trễ.
    #
    # Đo A/B (12 transcript THẬT từ prod + 2 ca dựng): tắt thinking → độ trễ trung vị
    # **4,61s → 1,43s (nhanh 3,2×)**, **14/14 quyết định TRÙNG nhau** trên cả 3 loại action
    # (clarify/follow_up/end), độ dài câu hỏi sinh ra gần như không đổi (97 → 96 ký tự).
    #
    # Vì sao decide-next KHÔNG cần suy luận sâu: nó chỉ chọn 1 trong 4 nhánh rồi viết một câu
    # hỏi ngắn — khác hẳn chấm điểm (cân nhắc nhiều tiêu chí) hay sinh bài giảng. CHỈ áp cho
    # đường này; các lượt gọi khác giữ nguyên mặc định.
    #
    # `0` = tắt · `>0` = trần token suy luận · `-1` = trả về mặc định động của model.
    # ⚠ Chỉ Gemini **Flash** cho phép 0; Pro không tắt được — đổi model thì phải xem lại.
    decide_next_thinking_budget: int = 0

    # ── F11: NGUỒN MỐC THỜI GIAN cho chỉ số cách nói ─────────────
    # `"vad"` (mặc định) = vùng tiếng nói do Silero VAD xác định · `"whisper"` = biên segment
    # Whisper (hành vi trước 2026-08-05, GIỮ LẠI CHỈ để quay lui không cần deploy).
    #
    # Vì sao đổi — đo trên 7 ghi âm THẬT lấy từ S3, trọng tài là hai bộ dò độc lập (ngưỡng năng
    # lượng + Silero) tự hiệu chuẩn đạt 0,02-0,03s trên audio biết trước sự thật và đồng ý với
    # nhau 18/21: biên segment Whisper bắt được **2/21 khoảng lặng (10%)**. Ca nặng nhất là một
    # câu trả lời 45s ngập ngừng 7 lần (có đoạn im 3 giây) bị báo về `pauseCount=0`,
    # `silenceRatio=0,020` trong khi thực tế là 0,315 — sai 16 lần, và luôn nghiêng về phía KHEN.
    # Đổi sang vùng VAD: lệch pauseCount 2,71 → 0,57 · lệch silenceRatio 0,152 → 0,035.
    #
    # KHÔNG phải lỗi model: `large-v3` cũng chỉ được 2/21 (Whisper học để CHÉP LỜI nên nó kéo
    # dài biên segment xuyên qua tiếng thở/tiếng phòng — trên audio tổng hợp có im lặng số tuyệt
    # đối thì nó cắt đúng, nên bài đo tổng hợp che mất hẳn lỗi này).
    #
    # ⚠ Mặc định BẬT, khác `grounding`/`tiering`/`cv_screening` (đều tắt): những cái đó là tính
    # năng MỚI, còn đây là bản vá cho hành vi ĐÃ ĐO ĐƯỢC LÀ SAI — để mặc định tắt tức là cố ý
    # giữ bug. Cờ này là cần gạt rollback, không phải cổng tính năng.
    delivery_metrics_source: str = "vad"

    # ── FACE VERIFY (SEC-2/3) ────────────────────────────────────
    # buffalo_l = pack insightface mặc định (detect + ArcFace embed). CPU-only.
    face_model_name: str = "buffalo_l"
    # Ngưỡng cosine-similarity coi là KHỚP mặt (≥ → match). Caller có thể override/request.
    face_match_threshold: float = 0.4

    # ── RABBITMQ CONFIG ──────────────────────────────────────────
    rabbitmq_url: str = "amqp://guest:guest@localhost/"
    queue_name: str = "scoring_pipeline_queue"   # TRÙNG RabbitMQ:QueueName .NET

    # Số message chấm xử lý ĐỒNG THỜI trên 1 tiến trình worker. Trước đây ghi cứng `1` với lý do
    # "chấm nặng" — lý do đó SAI khi đo thật: phần tốn thời gian là CHỜ MẠNG Gemini, không phải CPU.
    # Đo 2026-08-04 trên máy chạy worker: 1 lượt chấm 12,6s; 4 lượt SONG SONG cũng chỉ 13,3s
    # ⇒ throughput 4,8 → 18 lượt/phút mà không thêm phần cứng nào. `1` là lãng phí thuần.
    # ⚠ Trần này áp cho CẢ hai đường: đường THÍCH ỨNG gửi kèm transcript (bỏ Whisper — nhẹ, chỉ
    # chờ mạng) và đường TĨNH/republish KHÔNG có transcript (phải Whisper — nặng CPU thật). Đặt quá
    # cao thì một đợt job tĩnh sẽ chạy ngần đó Whisper cùng lúc và bóp nghẹt CPU máy chạy worker.
    # ⚠ Mỗi message = ≥1 lượt Gemini ⇒ giá trị này cũng là trần request đồng thời lên Gemini.
    scoring_prefetch: int = 10

    # ── DEAD-LETTER (AI2) ────────────────────────────────────────
    # Message bị nack(requeue=False) → broker đẩy sang DLX → DLQ thay vì XOÁ IM LẶNG.
    # 3 tên này khai vào `arguments` của queue chính; PHẢI TRÙNG y hệt args khai ở
    # .NET ScoringJobPublisher.cs — lệch 1 ký tự → RabbitMQ 406 PRECONDITION_FAILED
    # khi bên còn lại redeclare cùng queue với arguments khác.
    dlx_name: str = "scoring_pipeline_dlx"                 # dead-letter exchange (direct)
    dead_queue_name: str = "scoring_pipeline_dead_queue"  # nơi giữ message chết
    dead_routing_key: str = "scoring_dead"                # DLX → DLQ binding key

    # ── SÀNG CV B2B (C14) — queue RIÊNG, KHÔNG Whisper ───────────
    # TRÙNG hằng `CvScreeningPublisher.QueueName` (.NET). Publisher khai queue với
    # `arguments: null` ⇒ bên này PHẢI khai y hệt (durable, KHÔNG argument) — thêm
    # x-dead-letter-* ở đây sẽ ném PRECONDITION_FAILED 406 khi redeclare.
    cv_screening_queue_name: str = "cv_screening_queue"
    # Cao hơn scoring (prefetch=1): sàng CV KHÔNG tải audio/không Whisper nên nhẹ hơn nhiều.
    # Chạy trên channel RIÊNG để backlog audio không nghẽn sàng CV và ngược lại (ai.md).
    cv_screening_prefetch: int = 4
    # Bật/tắt consumer sàng CV mà không phải deploy lại. ⚠ Queue LIVE có thể đang tồn hàng trăm
    # message do StuckScreeningRepublisher đẩy lại mỗi 15' suốt thời gian KHÔNG có consumer —
    # mỗi message = 1 lượt Gemini. Xả queue trước khi bật lần đầu (xem ai.md §Pipeline sàng CV).
    # ⚠ Mặc định TẮT, theo đúng tiền lệ mọi rollout khác của repo (`GROUNDING_ENABLED`,
    # `TIERING_ENABLED`, `ADAPTIVE_ENABLED` đều false). Lý do cụ thể ở đây: lúc consumer này ra đời,
    # `cv_screening_queue` đã tồn 713 message của ĐÚNG 8 ứng viên (StuckScreeningRepublisher nhân bản
    # ~89 lần/người). Bật cùng lúc deploy = 713 lượt Gemini thay vì 8 (~2,85 triệu token thay vì ~32
    # nghìn). Trình tự an toàn: deploy code (tắt) → XẢ queue → bật `CV_SCREENING_ENABLED=true` →
    # republisher tự đẩy lại đúng 8 job trong 15'.
    cv_screening_enabled: bool = False

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

    # ── Chất lượng bài giảng roadmap (BC13/D20) ──────────────────────────
    # Bài trượt rubric (app/lesson_quality.py) được TRẢ LẠI kèm nhận xét và bắt viết lại. 2 = thử
    # tối đa 2 lượt; 1 = tắt hẳn việc viết lại (về hành vi cũ), vẫn giữ phần chấm.
    # Trần thấp có chủ đích: mỗi lượt là một lần gọi Gemini nằm TRONG request đồng bộ của người
    # học (đo thật 13-54s/lượt) — nới lên 3 là mời timeout quay lại.
    lesson_theory_max_attempts: int = 2


settings = Settings()
