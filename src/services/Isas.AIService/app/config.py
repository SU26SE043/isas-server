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

    # ── RETRY LỖI TẠM THỜI CỦA GEMINI (chokepoint `_generate`) ───
    # Log worker prod bắt được nguyên văn: `[⚠️] Lỗi tạm thời answer …: 503 UNAVAILABLE -> nack
    # (republish sau)`. Một cú 503 chớp nhoáng của Google KHÔNG được thử lại ở chỗ nó xảy ra:
    # message rơi thẳng xuống nhánh tạm thời → nack → phải chờ `StuckAnswerRepublisher` (.NET,
    # quét mỗi 2 phút) đẩy lại. Người dùng đo được **15 phút** chờ cho một sự cố kéo dài chưa
    # tới một giây — cứu hộ phía .NET là lưới an toàn cho lỗi kéo dài, dùng nó để đỡ một cú
    # nấc mạng là sai tầng.
    #
    # `3` = 1 lượt đầu + 2 lượt thử lại. Backoff NHÂN ĐÔI: chờ 1,0s rồi 2,0s ⇒ tối đa **3s nằm
    # chờ**, cộng phần gọi hỏng thì tổng cộng thêm ~5s cho ca xấu nhất.
    #
    # 🔴 RÀNG BUỘC CỨNG của hai con số này: `_generate` là chokepoint CHUNG nên nó nằm trên CẢ
    # `/decide-next` — đường chạy ĐỒNG BỘ trong request upload câu trả lời của người dùng, dưới
    # timeout **90s** phía .NET (cả request đo ~9,4s). Nới attempts/backoff là ăn thẳng vào ngân
    # sách đó và biến một cú 503 của Gemini thành timeout của .NET — hỏng to hơn cái đang vá.
    # Muốn kiên nhẫn hơn cho đường ASYNC (worker chấm) thì phải tách cấu hình theo đường gọi,
    # ĐỪNG nâng số dùng chung này.
    #
    # `1` = tắt hẳn việc thử lại (về đúng hành vi trước bản vá).
    gemini_retry_attempts: int = 3
    gemini_retry_backoff_seconds: float = 1.0

    # ── SCORING RETRY (AI3) ──────────────────────────────────────
    # score() raise ValueError khi LLM trả output không parse/không hợp lệ. Lỗi
    # parse thường CHỢP NHOÁNG (JSON cụt, thỉnh thoảng malformed) → thử lại vài
    # lần trước khi bó tay báo answer Failed. 3 = 1 lần đầu + 2 lần retry.
    #
    # ⚠ KHÁC HẲN `gemini_retry_attempts` ở trên, đừng gộp: vòng này thử lại OUTPUT hỏng (đã gọi
    # được Gemini, đã trả tiền token), vòng kia thử lại LỜI GỌI hỏng (chưa có output nào). Chính
    # vì thế `_generate` TUYỆT ĐỐI không bắt ValueError — bắt là hai vòng NHÂN nhau thành 9 lượt.
    score_max_attempts: int = 3

    # ── SC1c: VÒNG CHẤT LƯỢNG CÂU HỎI ────────────────────────────
    # Bộ câu hỏi không phủ đủ tiêu chí (`app/question_quality.coverage_defects`) được TRẢ LẠI kèm
    # nhận xét và sinh lại. `2` = 1 lượt đầu + tối đa 1 lượt sinh lại · `1` = TẮT hẳn việc sinh lại
    # (vẫn giữ phần kiểm — nó là hàm thuần, không tốn gì).
    #
    # ⚠ Đây là KILL-SWITCH của SC1c và nó **BẬT MẶC ĐỊNH**: mỗi lượt sinh lại là một lần gọi Gemini
    # nằm TRONG request tạo buổi luyện (đồng bộ). Trần thấp có chủ đích — mẫu `lesson_theory_max_attempts`.
    question_max_attempts: int = 2

    # ── QV1: CỔNG KIỂM CHỨNG CÂU HỎI ĐỐI CHIẾU CORPUS ────────────
    # BẬT ⇒ đổi hình dạng cả đường sinh, không chỉ thêm một bước:
    #   (a) grounding KHÔNG còn được cấp cho lượt SINH (câu hỏi sinh ra "tự do", corpus chỉ dùng để
    #       KIỂM) ⇒ prompt + response_schema + citations đều rẽ nhánh theo cờ này;
    #   (b) thêm MỘT lượt Gemini nữa cho mỗi lần sinh câu hỏi (`verify_questions`);
    #   (c) citations của kết quả đến TỪ lượt kiểm — lượt kiểm hỏng ⇒ trả về KHÔNG có citation
    #       (field biến mất), cố ý không dựng citation rỗng giả (D27).
    # ⚠ KHÔNG liên quan tới `question_max_attempts`: tắt cờ này KHÔNG tắt vòng sinh lại của SC1c.
    question_verify_enabled: bool = False


    # ── TTS: ĐỌC CÂU HỎI THÀNH TIẾNG ────────────────────────────
    # Dùng LẠI gemini_api_key ở trên → KHÔNG phải cấp credential mới.
    # Model TTS là model RIÊNG (model chat thường không nhận response_modalities=["AUDIO"]).
    tts_model: str = "gemini-2.5-flash-preview-tts"
    # Giọng dựng sẵn. Đổi voice ⇒ đổi cache key ⇒ audio cũ tự hết hiệu lực (khỏi purge tay).
    tts_voice: str = "Kore"
    # Gemini TTS hỗ trợ tiếng Việt (vi-VN). Đây là hằng phía SERVER — client KHÔNG truyền vào
    # (nếu sau này cho client chọn ngôn ngữ thì PHẢI đưa nó vào cache key, xem app/tts.py).
    tts_language_code: str = "vi-VN"
    tts_language_code_en: str = "en-US"
    # Tiền tố key cache S3 — key nội-dung-định-danh: tts/{sha256(voice+text)}.mp3
    tts_cache_prefix: str = "tts/"
    # Chủ động tổng hợp các câu vừa sinh ở nền. Nhờ vậy cache miss đắt/chậm không đợi tới lúc
    # ứng viên đã nhìn thấy câu hỏi mới bắt đầu gọi vendor.
    # ── ĐO TÁCH CHẶNG (một dòng log mỗi request) ─────────────────────
    # Bật mặc định: `/decide-next` có 10 chặng mà trước đây không đo được chặng nào, nên "chờ lâu"
    # chỉ có thể đoán. Chi phí là một `perf_counter` mỗi chặng + một dòng INFO mỗi request.
    # Tắt (`TIMING_LOG_ENABLED=false`) khi log quá ồn — lúc đó `timing.record` cũng thành no-op.
    timing_log_enabled: bool = True

    tts_prewarm_enabled: bool = True
    # Hai lane để câu 4/5 không phải chờ toàn bộ câu trước; vẫn đủ thấp để tránh burst quota.
    tts_prewarm_concurrency: int = 2
    # Câu adaptive chỉ xuất hiện SAU /decide-next, nên warm nền cùng lúc FE gọi /tts là quá muộn:
    # một lượt Gemini lạnh 8–10s sẽ đụng trần 9s của FE và rơi sang Web Speech. Giữ response
    # /decide-next tối đa thêm khoảng này để mp3 nằm sẵn trong cache trước khi câu hỏi tới browser.
    # Hết trần thì task vẫn chạy nền; không biến TTS thành điều kiện thành công của answer upload.
    tts_adaptive_prewarm_wait_seconds: float = 15.0
    # Redis CHỈ điều phối cache miss giữa nhiều replica; mp3 vẫn nằm ở S3/SeaweedFS. URL rỗng giữ
    # chế độ single-flight trong process cho local/test. Production compose nối `redis:6379`.
    tts_redis_enabled: bool = True
    tts_redis_url: str = ""
    tts_redis_key_prefix: str = "isas:tts:"
    # Lease phải dài hơn một lượt Gemini TTS lạnh; waiter chỉ chờ 8s để nằm dưới trần 9s của FE.
    # Hết 8s KHÔNG gọi vendor lần hai: trả lỗi để FE đọc fallback, owner vẫn làm cache ở nền.
    tts_redis_lock_ttl_seconds: float = 120.0
    tts_redis_wait_timeout_seconds: float = 8.0
    tts_redis_poll_interval_seconds: float = 0.1
    tts_redis_ready_ttl_seconds: int = 300
    # Redis lỗi không được kéo dài đường nóng: timeout nhỏ + circuit-break 5s rồi fail-open.
    tts_redis_socket_timeout_seconds: float = 0.25
    tts_redis_failure_cooldown_seconds: float = 5.0
    # Chặn task vendor treo vĩnh viễn và giữ distributed lock mãi. Lease 120s cố ý > trần này.
    tts_synthesis_timeout_seconds: float = 60.0

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

    # ── NGÂN SÁCH /ANALYZE-CV ─────────────────────────────────────
    # Requirement-mode là bài toán trích xuất có schema + hậu kiểm evidence xác định, không cần
    # hàng nghìn token suy luận ẩn. Prod 2026-08-18 đo 10 lượt: 5.077–9.238 output+thinking token
    # cho chỉ 7 requirement; một lượt chạy 52,5s rồi mới 502 ở bước map citation. `0` tắt thinking
    # trên Flash; `-1` trả về hành vi động của model để rollback không cần sửa code.
    analyze_cv_thinking_budget: int = 512

    # Phòng thủ tại chokepoint AIService: caller cũ/lệch vẫn có thể gửi `TopK × requirements`
    # chunk. InterviewService chọn round-robin trước, còn cap này bảo đảm prompt không phình lại khi
    # có caller khác. `0` = bỏ grounding của CV analysis; `-1` = không giới hạn (hành vi cũ).
    analyze_cv_max_grounding_chunks: int = 8

    # ── NGÂN SÁCH "THINKING" CHO CHẤM ĐIỂM (`score`) ─────────────
    # Chấm MỘT câu trả lời mất **19,6s (p50)** trên prod, và `ai_usage_logs` chỉ thẳng chỗ chảy:
    # operation `score` có output p50 **3.570 token**, trong khi `decide_next` (đã đặt trần 0)
    # chỉ **126**. `output_tokens = candidates + thoughts` (xem `app/usage.py`) nên con số đó ĐÃ
    # gộp token suy luận; phần nhìn thấy được (điểm + reasoning + sampleAnswer) đo trong DB chỉ
    # ~900–1.000 token ⇒ **~2.500 token là suy luận ẩn** — không ai đọc, mà vẫn tính tiền theo
    # giá output VÀ vẫn nằm trong thời gian ứng viên ngồi chờ.
    #
    # `score()` là đường gọi Gemini DUY NHẤT còn chưa có trần: `decide_next` và `analyze_cv` đã
    # đặt từ các vòng trước.
    #
    # `512` chứ KHÔNG phải `0` như decide_next: chấm là cân nhắc nhiều tiêu chí, mỗi tiêu chí một
    # thang mức, và mỗi mức phải kèm dẫn chứng lấy từ transcript (E11) — khác hẳn decide_next
    # (chọn 1 trong 4 nhánh rồi viết một câu hỏi ngắn). Cắt sạch suy luận ở đây là đánh đổi vào
    # ĐỘ ĐÚNG CỦA ĐIỂM, thứ đắt nhất service này bán. Cùng con số + cùng lý lẽ với
    # `analyze_cv_thinking_budget`.
    #
    # `0` = tắt · `>0` = trần token suy luận · `-1` = trả về mặc định động của model (cần gạt
    # quay lui, không phải sửa code + deploy lại).
    score_thinking_budget: int = 512

    # ── Q16: SỐ LƯỢT SINH CÂU ĐÀO SÂU ────────────────────────────
    # `/decide-next` TỪNG là đường DUY NHẤT của provider không có retry: output hỏng một lượt là
    # raise thẳng → 502. Với `score()` (`score_max_attempts=3`) và `generate_lesson_theory`
    # (`lesson_theory_max_attempts=2`) thì output hỏng chợp nhoáng đã được thử lại từ lâu — ở đây
    # thì không, dù hậu quả nhìn thấy được nhiều hơn: ứng viên nhận nửa câu hỏi rồi trả lời nó.
    #
    # `2` chứ không phải `3`: đường này chạy ĐỒNG BỘ trong request upload câu trả lời (đo trên prod
    # 2026-08-05: một lượt ~1,43s sau khi tắt thinking, cả request ~9,4s) ⇒ mỗi lượt thêm là độ trễ
    # cộng thẳng vào trải nghiệm. `1` = tắt hẳn việc thử lại (về hành vi cũ), vẫn giữ phần kiểm.
    decide_next_max_attempts: int = 2

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

    # ── CỔNG IM LẶNG ─────────────────────────────────────────────
    # Bản ghi mà VAD không thấy vùng tiếng nói nào ⇒ KHÔNG chép lời, trả `reject_reason="no_speech"`
    # để .NET đánh answer `Skipped` (không chấm, không trừ theo PAY-13).
    #
    # 🔴 Vì sao phải chặn ở đây thay vì tin bộ chấm: đo trên prod 2026-08-15, một bản ghi im lặng 8
    # giây ra transcript "Hãy subscribe cho kênh Ghiền Mì Gõ…" (vết bẩn dữ liệu huấn luyện của
    # Whisper) và ĐƯỢC CHẤM ĐIỂM THẬT trên cả 5 tiêu chí. Bộ chấm không có cách nào biết câu đó do
    # máy bịa; VAD thì biết chắc — và nó đã chạy sẵn cho F11, nên cổng này gần như miễn phí.
    #
    # ⚠ Mặc định BẬT — cùng lý do `delivery_metrics_source`: đây là bản vá cho hành vi ĐÃ ĐO ĐƯỢC
    # LÀ SAI, không phải tính năng mới. Đặt `SILENCE_GATE_ENABLED=false` để quay lui.
    silence_gate_enabled: bool = True

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

    # ── Chấm thử rubric (E9b) — sinh 3 bài mẫu yếu/khá/xuất sắc ──────────
    # LLM mặc định viết yếu=ngắn, giỏi=dài ⇒ bài kiểm chứng sẽ đo ĐỘ DÀI chứ không đo thước đo;
    # tệ hơn, nếu bộ chấm CŨNG thưởng độ dài thì dải điểm đẹp lại đi XÁC NHẬN một thước đo hỏng.
    # Lệch quá ngưỡng → sinh lại đúng 1 lượt kèm nhận xét; vẫn lệch → GIAO HÀNG kèm cờ cảnh báo
    # (không giấu, và cũng không 502 — HR vẫn cần xem được bài).
    # Trần thấp có chủ đích như `lesson_theory_max_attempts`: mỗi lượt là một lần gọi Gemini nằm
    # TRONG request đồng bộ mà HR đang ngồi chờ (đã tốn thêm 3-4 lượt chấm phía sau).
    preview_answers_max_attempts: int = 2

    # ── TRẦN THREAD CHO CÔNG VIỆC CHẶN ────────────────────────────
    # `asyncio.to_thread` dùng executor mặc định của event loop, cỡ `min(32, cpu_count + 4)`
    # (CPython `Lib/concurrent/futures/thread.py`) ⇒ **12** trên server 8 core. Việc CHẶN của
    # service đi qua đó: đọc S3 và chép lời ĐỒNG BỘ (httpx blocking). Gemini thì KHÔNG —
    # `_generate` dùng client async (`_client.aio.…`) nên không giữ thread nào.
    #
    # **0 = giữ nguyên mặc định asyncio.** Production đặt **32** qua env (đo bên dưới).
    #
    # ── SỐ ĐO THẬT (2026-08-06, server 8 core / 7,6 GB, `TRANSCRIBE_PROVIDER=whisper-1`) ──
    # Đo bằng `scripts/loadtest-ai-threadpool.py` trên `/transcribe` (cùng đường chặn, không
    # lẫn chi phí Gemini), audio THẬT 18,3s lấy từ S3:
    #
    #   một request giữ thread   3,9s  (S3 0,2s + chép lời 3,7s — đo 3 clip 18-23s)
    #   K=32  pool 12 (mặc định) 3,22 req/s   p50 7,52s   p95 9,67s
    #   K=32  pool 32            4,82 req/s   p50 5,03s   p95 7,40s     ← +50%
    #   K=48  pool 32            4,96 req/s   p50 6,00s   p95 8,52s     ← chỉ +3%: ĐÃ BÃO HOÀ
    #   (sau khi áp lên prod, K=32: 5,16 req/s — p50 4,61s, p95 6,95s, 0 lỗi)
    #
    # ⚠ **ĐỪNG nâng lên 64.** Ở pool 32, tăng tải 32→48 chỉ được +3% mà độ trễ tệ đi ⇒ trần thật
    # (~5 req/s) nằm SAU pool, không phải ở pool: 32 thread mà chỉ đạt 4,9 req/s thì thêm thread
    # chỉ dài thêm hàng đợi. Nghi can là chính whisper-1 (băng thông đo được 5,75 MB/s, mà 5 req/s
    # × 586 KB = 2,9 MB/s — mới một nửa). Muốn vượt trần này thì cần gạt là
    # `transcribe_send_original` (WAV 586 KB → opus ~60 KB cho cùng 18s), không phải pool.
    #
    # ⚠ Ước lượng CŨ trong comment này ("trần ~2,26 req/s do băng thông") là SAI vì tính cho câu
    # trả lời 90 giây; câu thật đo được chỉ **18,5s** (p50, n=34 `ai_usage_logs.audio_seconds`)
    # ⇒ payload 586 KB chứ không phải 2,88 MB. Con số RAM "15 MB/request" cũng theo đó: đo thật
    # 48 request đang bay chỉ thêm ~80 MB (~1,7 MB/request), đỉnh 681 MB, CPU 208%/800%.
    #
    # ⚠ Ở worker gần như vô hiệu: trần thật bên đó là `scoring_prefetch` (10) + `cv_screening_prefetch` (4).
    thread_pool_max_workers: int = 0


settings = Settings()
