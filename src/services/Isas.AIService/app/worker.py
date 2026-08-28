import asyncio
import json
import os
import tempfile
import boto3
import aiohttp
import aio_pika

from app.config import settings
from app import threadpool
from app.cv_screening import maybe_start_cv_screening_consumer
from app.multi_voice import maybe_report_multi_voice
from app.providers.gemini import GeminiProvider
from app.transcriber import NO_SPEECH, Transcriber

transcriber = Transcriber()
provider = GeminiProvider()

s3_client = boto3.client(
    's3',
    endpoint_url=settings.s3_endpoint,
    aws_access_key_id=settings.s3_access_key,
    aws_secret_access_key=settings.s3_secret_key,
)


class PermanentError(Exception):
    """Lỗi KHÔNG thể khắc phục bằng retry (audio hỏng, LLM output sai theo cách
    tái lập). Worker sẽ báo .NET đánh dấu answer Failed thay vì retry vô tận."""


class NoSpeechError(PermanentError):
    """Bản ghi KHÔNG có tiếng nói (VAD) — không phải sự cố hệ thống.

    Vẫn là "vĩnh viễn" (chép lại bao nhiêu lần cũng thế) nên đi chung đường PermanentError, nhưng
    .NET phải đánh answer ``Skipped`` chứ không ``Failed``: người luyện đọc lịch sử cần thấy "câu
    này không có câu trả lời", không phải "hệ thống hỏng". Về TIỀN hai nhãn như nhau (PAY-13 chỉ
    hỏi có answer nào ``Scored`` không)."""


def make_score_payload(answer_id, transcript, rubric_version, scores, attempt_no,
                       sample_answer=None, delivery_metrics=None, prompt_version=None,
                       transcript_engine=None) -> dict:
    """E10 — dựng body callback chấm gửi về .NET. Echo ``attemptNo`` (từ job) để .NET lưu điểm
    theo đúng attempt (self-consistency chấm N lần → median/tiêu chí + cờ needs_review).
    Tách hàm thuần để unit-test không cần dựng cả pipeline worker.

    F13 — ``sampleAnswer``: câu trả lời mẫu do CÙNG lượt chấm sinh ra. Optional (default None)
    để mọi call site positional cũ không phải sửa; .NET bỏ qua khi rỗng.

    F11 — ``deliveryMetrics``: chỉ số cách nói (tốc độ nói/khoảng lặng/từ đệm) của CHÍNH lượt
    transcribe đã dùng để chấm. Optional; .NET lưu lên answer để hiện cho người luyện (FR06).

    BK23 — ``promptVersion``: con dấu phiên bản prompt của CHÍNH lượt chấm này (``score()`` chụp
    tại chỗ dựng prompt). .NET đóng lên từng dòng ``answer_scores`` để sau này trả lời được "hai
    điểm này có cùng thước đo không". Optional (default None): worker cũ không gửi → .NET để NULL
    = "chấm trước F21/BK23, không biết prompt nào" — phân biệt được với 0 = "bản mặc định thuần".

    ``transcriptEngine``: engine ĐÃ CHÉP RA transcript đang chấm ("whisper-1" /
    "gemini-2.5-flash" / "local:small"). 🔴 Tên khoá là HỢP ĐỒNG DÂY với .NET — đổi tên KHÔNG ném
    lỗi, nó chỉ làm .NET bind hụt rồi lưu NULL vĩnh viễn (đúng lớp bug ``focusCriteria`` bị
    pydantic nuốt). Cần vì đường chép lời nay có DỰ PHÒNG: khi nhà cung cấp từ xa hỏng, bản chép
    lặng lẽ rơi về Whisper cục bộ (lỗi từ 4,2% so với 0,7%) mà nhìn từ ngoài hai bản giống hệt
    nhau — thiếu con dấu thì "điểm thấp do ứng viên hay do bản chép?" là câu không trả lời được.
    Optional (default None): None = không biết, khác hẳn một tên engine cụ thể."""
    return {
        "answerId": answer_id,
        "transcript": transcript,
        "rubricVersion": rubric_version,
        "scores": scores,       # [{criterionId, score, levelMatched, reasoning}, ...] (E9 shape)
        "attemptNo": attempt_no,
        "sampleAnswer": sample_answer,
        "deliveryMetrics": delivery_metrics,
        "promptVersion": prompt_version,
        "transcriptEngine": transcript_engine,
    }


async def post_callback(payload: dict):
    """Gửi kết quả về .NET. .NET là chủ DB — Python KHÔNG ghi DB thẳng."""
    url = f"{settings.dotnet_callback_base}/internal/answers/{payload['answerId']}/result"
    headers = {"X-Internal-Token": settings.internal_token}
    async with aiohttp.ClientSession() as session:
        async with session.post(url, json=payload, headers=headers) as resp:
            if resp.status >= 300:
                text = await resp.text()
                raise RuntimeError(f"Callback fail {resp.status}: {text}")
            print(f"[🎉] Callback .NET OK cho Answer {payload['answerId']}")


async def post_failed(answer_id, reason: str, no_speech: bool = False):
    """Báo .NET đánh dấu answer Failed (lỗi chấm vĩnh viễn) để session thoát kẹt.

    ``no_speech=True`` ⇒ .NET đánh ``Skipped`` thay vì ``Failed`` (bản ghi im lặng, không phải sự
    cố). 🔴 Khoá dây là ``noSpeech`` (camelCase) — khớp `AnswerFailedCallbackRequest.NoSpeech`;
    đổi tên KHÔNG ném lỗi, .NET chỉ bind hụt rồi rơi về ``Failed`` như cũ."""
    url = f"{settings.dotnet_callback_base}/internal/answers/{answer_id}/failed"
    headers = {"X-Internal-Token": settings.internal_token}
    async with aiohttp.ClientSession() as session:
        async with session.post(
                url, json={"reason": reason, "noSpeech": no_speech}, headers=headers) as resp:
            if resp.status >= 300:
                text = await resp.text()
                raise RuntimeError(f"Callback failed-endpoint fail {resp.status}: {text}")
            print(f"[⛔] Đã báo .NET Failed cho Answer {answer_id}")


async def process_message(message: aio_pika.IncomingMessage):
    # KHÔNG dùng `async with message.process()` mặc định ack ngay.
    # Ta ack/nack thủ công để message được retry nếu xử lý lỗi.
    try:
        body = json.loads(message.body.decode())
        print(f"\n[🚀] Message từ C#: {body}")

        answer_id = body.get("answerId") or body.get("AnswerId")
        storage_path = body.get("audioObjectKey") or body.get("AudioObjectKey")
        question_content = body.get("questionContent") or body.get("QuestionContent")
        job_category = body.get("jobCategory") or body.get("JobCategory")
        criteria = body.get("criteria") or body.get("Criteria") or []
        rubric_version = body.get("rubricVersion") or body.get("RubricVersion")

        # Đáp án mẫu HR soạn cho ĐÚNG câu này (B2B). None với câu B2C, câu đào sâu AI sinh lúc thi,
        # chiến dịch chưa soạn, hoặc kill-switch `Scoring:UseSampleAnswer` phía .NET đang tắt.
        # ⚠ Đọc CẢ HAI kiểu viết hoa/thường như `transcript` và `deliveryMetrics` ngay dưới:
        # `ScoringJobPublisher` serialize job KHÔNG kèm options nên khoá trên hàng đợi là PascalCase,
        # trong khi các đường khác dùng camelCase. Chỉ đọc một kiểu là field chết im lặng.
        sample_answer = body.get("sampleAnswer") or body.get("SampleAnswer")
        sample_answer = sample_answer.strip() if isinstance(sample_answer, str) else None

        # E10 — self-consistency: attempt worker phải chấm + nhiệt độ (attempt 1 = 0 tái lập,
        # 2..N > 0 dao động). Job cũ (E9) không có 2 field này → attempt_no=1, temperature=0.
        attempt_no = body.get("attemptNo") or body.get("AttemptNo") or 1
        temperature = body.get("temperature")
        if temperature is None:
            temperature = body.get("Temperature")
        temperature = float(temperature) if temperature is not None else 0.0

        # Phỏng vấn THÍCH ỨNG: Interview đã transcribe ĐỒNG BỘ khi upload (qua /decide-next)
        # và gửi kèm transcript → worker BỎ QUA Whisper (single-source transcript; tiết kiệm N
        # lần transcribe của self-consistency E10). Không có (đường cũ / republish job cũ thiếu
        # transcript) → tải audio + Whisper như trước.
        pre_transcript = body.get("transcript") or body.get("Transcript")
        pre_transcript = pre_transcript.strip() if isinstance(pre_transcript, str) else None

        # F11 — chỉ số cách nói đã đo sẵn ở /decide-next (đường THÍCH ỨNG). Bắt buộc phải đi kèm
        # transcript: worker bỏ Whisper khi có transcript ⇒ nếu không nhận chỉ số ở đây thì buổi
        # adaptive VĨNH VIỄN không có chỉ số trong khi buổi tĩnh lại có — hỏng âm thầm, không lỗi.
        pre_metrics = body.get("deliveryMetrics") or body.get("DeliveryMetrics")
        if not isinstance(pre_metrics, dict):
            pre_metrics = None

        # Con dấu engine của bản chép ĐÃ CÓ SẴN (đường thích ứng: /decide-next chép, .NET lưu rồi
        # gửi kèm job). Đi cùng `pre_transcript` chứ không đo lại được ở đây — worker bỏ Whisper
        # khi job đã mang transcript, nên nó KHÔNG biết bản chép đó do engine nào tạo ra.
        # Job cũ / .NET chưa deploy phần này → None = "không biết" (không bịa "local").
        #
        # 🔴 ĐỌC CẢ HAI CASING, và PascalCase mới là bản THẬT trên dây này. `ScoringJobPublisher.cs`
        # gọi `JsonSerializer.Serialize(job)` KHÔNG truyền options ⇒ dùng `JsonSerializerOptions.
        # Default` (PascalCase), khác hẳn đường HTTP của ASP.NET Core (camelCase qua Web defaults).
        # Chỉ đọc camelCase thì con dấu chết IM LẶNG trên đường queue — không lỗi, không cảnh báo,
        # chỉ là một cột NULL. Đúng mẫu phòng thủ đã có sẵn cho `transcript`/`deliveryMetrics` ngay
        # bên trên; sang HTTP thì GỬI camelCase.
        pre_engine = body.get("transcriptEngine") or body.get("TranscriptEngine")
        pre_engine = pre_engine.strip() if isinstance(pre_engine, str) and pre_engine.strip() else None
        language = body.get("language") or body.get("Language") or "vi"

        # J5 — cấp độ ứng viên, CHỈ có ở buổi B2C (.NET set None cho mọi buổi B2B — CAMP-10).
        # Đọc CẢ HAI casing như mọi field khác trên hàng đợi (`ScoringJobPublisher` serialize
        # PascalCase, xem ghi chú `pre_engine` ngay trên).
        seniority = body.get("seniority") or body.get("Seniority")
        seniority = seniority.strip() if isinstance(seniority, str) and seniority.strip() else None

        # Cần answerId luôn; cần audioObjectKey CHỈ khi chưa có transcript sẵn.
        if not answer_id or (not storage_path and not pre_transcript):
            print("[❌] Thiếu answerId/audioObjectKey/transcript — bỏ message (không retry).")
            await message.ack()  # message hỏng vĩnh viễn, ack để khỏi lặp vô hạn
            return

        tmp_path = None

        async def ensure_audio():
            """Tải audio về file tạm, idempotent trong MỘT lượt xử lý message.

            Tách ra vì nay có HAI người dùng: đường chép lời (bên dưới) và detector multi_voice
            (AC1/B5). Đường THÍCH ỨNG bỏ qua Whisper hoàn toàn nên ở đó chưa ai tải audio —
            detector phải tự tải, nhưng CHỈ khi nó thực sự chạy (B2B + attempt 1 + cờ bật), nên
            đây là callable LƯỜI chứ không phải một lượt tải vô điều kiện.
            """
            nonlocal tmp_path
            if tmp_path:
                return tmp_path
            if not storage_path:
                return None
            suffix = os.path.splitext(storage_path)[1] or ".webm"
            with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
                # Gán `tmp_path` TRƯỚC lượt tải, không phải sau: `delete=False` nên file chỉ được
                # dọn ở `finally` bên dưới, mà `finally` chỉ dọn được cái tên nó BIẾT. Tải hỏng
                # giữa chừng (503/mạng) mà gán sau thì file rác nằm lại /tmp vĩnh viễn, mỗi lượt
                # retry thêm một cái.
                tmp_path = tmp.name
                # 🔴 boto3 là BLOCKING. Gọi thẳng trên event loop thì suốt lượt tải, cả
                # `scoring_prefetch` (10) coroutine còn lại ĐỨNG HÌNH — kể cả những lượt chỉ đang
                # CHỜ MẠNG Gemini, tức là đúng phần song song mà prefetch=10 mua về (đo 2026-08-04:
                # 4 lượt song song 13,3s vs 1 lượt 12,6s).
                await asyncio.to_thread(
                    s3_client.download_fileobj, settings.s3_bucket, storage_path, tmp)
            return tmp_path

        try:
            delivery = pre_metrics   # F11 — mặc định dùng bản đo sẵn (đường thích ứng)
            engine = pre_engine      # con dấu engine đi kèm transcript có sẵn (nếu có)
            if pre_transcript:
                transcript = pre_transcript
                print(f"[✅] Dùng transcript có sẵn (bỏ qua Whisper): {transcript[:80]}")
                if delivery is None:
                    # Job cũ / Interview chưa deploy bản F11 → không có chỉ số. KHÔNG transcribe
                    # lại chỉ để lấy chỉ số (mất trọn cái lợi bỏ Whisper của INT-17) — chấm không
                    # có số đo, prompt sẽ nói rõ "chưa đo được" thay vì để LLM bịa.
                    print("[⚠️] Không có deliveryMetrics kèm transcript (job cũ?) — chấm không số đo")
            else:
                # 1. Tải audio từ SeaweedFS (lỗi ở đây = tạm thời -> để retry).
                await ensure_audio()
                print(f"[*] Tải file OK: {tmp_path}")

                # 2. Whisper transcribe — audio hỏng/không nghe được = lỗi VĨNH VIỄN.
                #    F11: transcribe_detailed giữ mốc thời gian segment → đo chỉ số cách nói
                #    NGAY TẠI ĐÂY (đường tĩnh). Không tốn thêm lượt Whisper nào: mốc segment
                #    vốn đã có sẵn, trước F11 chỉ bị vứt đi khi nối text.
                try:
                    result = await asyncio.to_thread(
                        transcriber.transcribe_detailed, tmp_path, language)
                    transcript = result.text
                    delivery = result.metrics.to_dict() if result.metrics else None
                    # Lượt chép lời DIỄN RA Ở ĐÂY nên con dấu lấy thẳng từ kết quả — có thể là
                    # bản dự phòng cục bộ nếu nhà cung cấp từ xa vừa hỏng.
                    engine = result.engine
                except Exception as e:
                    raise PermanentError(f"Transcribe lỗi (audio hỏng?): {e}")

                # Bản chép bị TỪ CHỐI (cổng im lặng / cả hai engine ra rác). Phân biệt hai nhãn:
                # im lặng là chuyện của người trả lời (Skipped), rác là hỏng hóc kỹ thuật (Failed).
                if result.reject_reason == NO_SPEECH:
                    raise NoSpeechError("Bản ghi không có tiếng nói (VAD)")
                if result.reject_reason is not None:
                    raise PermanentError(
                        f"Bản chép không dùng được: {result.reject_reason}")

                if not transcript or not transcript.strip():
                    raise PermanentError("Transcript rỗng — audio không nghe được")
                print(f"[✅] Transcript: {transcript}")

            # 3. Gemini chấm theo rubric.
            #    score() raise ValueError khi LLM trả output không parse/không hợp lệ.
            #    AI3 — lỗi parse thường CHỢP NHOÁNG (JSON cụt, thỉnh thoảng malformed),
            #    nên thử lại tối đa `score_max_attempts` lần (GIỮ NGUYÊN args, kể cả
            #    temperature/self-consistency E10) trước khi bó tay -> PermanentError -> Failed.
            #    Lỗi gọi API (rate limit/5xx/mạng) KHÔNG phải ValueError -> rơi xuống
            #    handler tạm thời -> nack để republisher thử lại sau.
            outcome = None
            for score_try in range(1, settings.score_max_attempts + 1):
                try:
                    outcome = await provider.score(
                        question=question_content,
                        transcript=transcript,
                        job_category=job_category,
                        criteria=criteria,
                        temperature=temperature,   # E10: attempt 1 = 0, 2..N > 0
                        delivery=delivery,         # F11: số đo cách nói (None = chưa đo được)
                        language=language,
                        sample_answer=sample_answer,
                        seniority=seniority,       # J5: None ⇒ B2B, không hiệu chỉnh theo cấp độ
                    )
                    break
                except ValueError as e:
                    if score_try >= settings.score_max_attempts:
                        raise PermanentError(
                            f"Chấm thất bại sau {settings.score_max_attempts} lần "
                            f"(LLM output không hợp lệ): {e}")
                    print(f"[↻] Chấm lỗi parse lần {score_try}/"
                          f"{settings.score_max_attempts} (thử lại): {e}")
            print(f"[✅] Chấm xong (attempt {attempt_no}): {outcome.scores}")
            if not outcome.sample_answer:
                # F13 — không chặn luồng (câu trả lời mẫu là phụ trợ), nhưng phải THẤY được
                # khi LLM im lặng bỏ field: im lặng ở đây = tính năng chết mà không ai biết.
                print(f"[⚠️] Không có câu trả lời mẫu (F13) cho answer {answer_id}")

            # 4. Callback về .NET — .NET ghi transcript + answer_scores + đổi status.
            #    Lỗi gửi callback = tạm thời -> retry. E10: echo attemptNo để .NET lưu theo attempt.
            await post_callback(make_score_payload(
                answer_id, transcript, rubric_version, outcome.scores, attempt_no,
                sample_answer=outcome.sample_answer, delivery_metrics=delivery,
                prompt_version=outcome.prompt_version, transcript_engine=engine))

            await message.ack()

            # AC1/B5 — phát hiện ≥2 giọng nói (cờ `multi_voice` cho HR). Đặt SAU `ack()` có chủ ý:
            # lượt chấm là ĐƯỜNG TIỀN (PAY-13) và nó đã xong, nên không việc gì phải giữ message
            # chờ một tính năng THỬ NGHIỆM mặc định tắt. `maybe_report_multi_voice` nuốt mọi
            # exception và tự lo ba cổng (cờ bật · B2B · attempt 1) nên nhánh này không thêm được
            # đường hỏng nào cho việc chấm.
            await maybe_report_multi_voice(body, ensure_audio)

        except PermanentError as e:
            # Không retry được -> báo .NET đánh dấu Failed rồi ack (bỏ message).
            # Nếu báo Failed cũng fail (mạng) -> nack để vòng sau thử lại.
            print(f"[⛔] Lỗi vĩnh viễn answer {answer_id}: {e}")
            try:
                await post_failed(answer_id, str(e), no_speech=isinstance(e, NoSpeechError))
                await message.ack()
            except Exception as report_err:
                print(f"[❌] Báo Failed không được: {report_err} -> nack")
                await message.nack(requeue=False)

        except Exception as e:
            # Lỗi tạm thời (S3/Gemini API/mạng) -> nack. StuckAnswerRepublisher sẽ
            # đẩy lại sau. requeue=False để không lặp nóng ngay trên cùng message.
            print(f"[⚠️] Lỗi tạm thời answer {answer_id}: {e} -> nack (republish sau)")
            await message.nack(requeue=False)
        finally:
            if tmp_path and os.path.exists(tmp_path):
                os.remove(tmp_path)

    except Exception as e:
        print(f"[❌] Message không đọc được: {e}")
        await message.ack()  # body hỏng, ack để khỏi kẹt queue


async def declare_topology(channel):
    """AI2 — khai DLX + DLQ + queue chính (mang args dead-letter) và trả queue chính.

    Message bị ``nack(requeue=False)`` (2 chỗ trong process_message) TRƯỚC ĐÂY bị broker
    XOÁ IM LẶNG (queue không có DLX). Nay queue chính trỏ dead-letter về DLX
    ``scoring_pipeline_dlx`` → DLQ ``scoring_pipeline_dead_queue`` để giữ lại soi/replay tay.

    HÀNH VI dead-letter (1 DLX gắn trên queue → CẢ HAI nack site đều rơi vào DLQ):
      • worker.py `except PermanentError` → nack: lỗi VĨNH VIỄN mà báo Failed cho .NET
        cũng fail (mạng) — đây là giá trị CHÍNH của DLQ (không thì mất hẳn).
      • worker.py nhánh Exception tạm thời → nack: message này CŨNG vào DLQ, NHƯNG
        `StuckAnswerRepublisher` (.NET, quét mỗi 2') vẫn publish lại bản MỚI → answer vẫn
        được chấm; bản trong DLQ chỉ là bản sao để soi. CHẤP NHẬN overlap này (đơn giản,
        đúng — không tách 2 DLX chỉ để loại bản sao transient).

    Tách hàm để unit-test topology bằng AsyncMock channel (không cần broker sống).
    args PHẢI trùng khai ở .NET ScoringJobPublisher.cs, nếu không → 406 khi redeclare queue.
    """
    dlx = await channel.declare_exchange(
        settings.dlx_name, aio_pika.ExchangeType.DIRECT, durable=True)
    dead_queue = await channel.declare_queue(settings.dead_queue_name, durable=True)
    await dead_queue.bind(dlx, routing_key=settings.dead_routing_key)
    queue = await channel.declare_queue(
        settings.queue_name,
        durable=True,
        arguments={
            "x-dead-letter-exchange": settings.dlx_name,
            "x-dead-letter-routing-key": settings.dead_routing_key,
        },
    )
    return queue


async def main():
    # Đối xứng với api. ⚠ Ở worker cần gạt này gần như vô hiệu: trần thật bên đây là
    # `scoring_prefetch` (10) + `cv_screening_prefetch` (4), không phải số thread.
    threadpool.apply(asyncio.get_running_loop(), settings.thread_pool_max_workers)
    print("[*] Kết nối RabbitMQ...")
    connection = await aio_pika.connect_robust(settings.rabbitmq_url)
    async with connection:
        channel = await connection.channel()
        # `queue.consume(callback)` (KHÔNG phải async-iterator) → aio-pika chạy các callback
        # ĐỒNG THỜI, số lượng do đúng prefetch này chặn. Xem config.scoring_prefetch để biết vì sao
        # bỏ giá trị cũ `1` (đo thật: 4 lượt song song ≈ thời gian 1 lượt, vì chờ mạng chứ không CPU).
        await channel.set_qos(prefetch_count=settings.scoring_prefetch)
        queue = await declare_topology(channel)  # AI2: DLX/DLQ + queue chính (args dead-letter)
        await queue.consume(process_message)
        print(f"[✅] Worker chạy, nghe queue '{settings.queue_name}' (CTRL+C để thoát)")

        # C14 — sàng CV B2B trong CÙNG tiến trình nhưng CHANNEL RIÊNG: prefetch riêng (sàng CV
        # nhẹ hơn nhiều vì không Whisper) để backlog audio không nghẽn sàng CV và ngược lại.
        # Cổng kill-switch nằm TRONG hàm (không phải `if` ở đây) để cờ tắt unit-test được —
        # xem docstring `maybe_start_cv_screening_consumer`.
        await maybe_start_cv_screening_consumer(connection)

        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
