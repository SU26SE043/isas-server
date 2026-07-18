import asyncio
import json
import os
import tempfile
import boto3
import aiohttp
import aio_pika

from app.config import settings
from app.providers.gemini import GeminiProvider
from app.transcriber import Transcriber

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


def make_score_payload(answer_id, transcript, rubric_version, scores, attempt_no) -> dict:
    """E10 — dựng body callback chấm gửi về .NET. Echo ``attemptNo`` (từ job) để .NET lưu điểm
    theo đúng attempt (self-consistency chấm N lần → median/tiêu chí + cờ needs_review).
    Tách hàm thuần để unit-test không cần dựng cả pipeline worker."""
    return {
        "answerId": answer_id,
        "transcript": transcript,
        "rubricVersion": rubric_version,
        "scores": scores,       # [{criterionId, score, levelMatched, reasoning}, ...] (E9 shape)
        "attemptNo": attempt_no,
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


async def post_failed(answer_id, reason: str):
    """Báo .NET đánh dấu answer Failed (lỗi chấm vĩnh viễn) để session thoát kẹt."""
    url = f"{settings.dotnet_callback_base}/internal/answers/{answer_id}/failed"
    headers = {"X-Internal-Token": settings.internal_token}
    async with aiohttp.ClientSession() as session:
        async with session.post(url, json={"reason": reason}, headers=headers) as resp:
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

        # Cần answerId luôn; cần audioObjectKey CHỈ khi chưa có transcript sẵn.
        if not answer_id or (not storage_path and not pre_transcript):
            print("[❌] Thiếu answerId/audioObjectKey/transcript — bỏ message (không retry).")
            await message.ack()  # message hỏng vĩnh viễn, ack để khỏi lặp vô hạn
            return

        tmp_path = None
        try:
            if pre_transcript:
                transcript = pre_transcript
                print(f"[✅] Dùng transcript có sẵn (bỏ qua Whisper): {transcript[:80]}")
            else:
                # 1. Tải audio từ SeaweedFS (lỗi ở đây = tạm thời -> để retry).
                suffix = os.path.splitext(storage_path)[1] or ".webm"
                with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
                    s3_client.download_fileobj(settings.s3_bucket, storage_path, tmp)
                    tmp_path = tmp.name
                print(f"[*] Tải file OK: {tmp_path}")

                # 2. Whisper transcribe — audio hỏng/không nghe được = lỗi VĨNH VIỄN.
                try:
                    transcript = await asyncio.to_thread(transcriber.transcribe, tmp_path, "vi")
                except Exception as e:
                    raise PermanentError(f"Transcribe lỗi (audio hỏng?): {e}")
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
            scores = None
            for score_try in range(1, settings.score_max_attempts + 1):
                try:
                    scores = await provider.score(
                        question=question_content,
                        transcript=transcript,
                        job_category=job_category,
                        criteria=criteria,
                        temperature=temperature,   # E10: attempt 1 = 0, 2..N > 0
                    )
                    break
                except ValueError as e:
                    if score_try >= settings.score_max_attempts:
                        raise PermanentError(
                            f"Chấm thất bại sau {settings.score_max_attempts} lần "
                            f"(LLM output không hợp lệ): {e}")
                    print(f"[↻] Chấm lỗi parse lần {score_try}/"
                          f"{settings.score_max_attempts} (thử lại): {e}")
            print(f"[✅] Chấm xong (attempt {attempt_no}): {scores}")

            # 4. Callback về .NET — .NET ghi transcript + answer_scores + đổi status.
            #    Lỗi gửi callback = tạm thời -> retry. E10: echo attemptNo để .NET lưu theo attempt.
            await post_callback(make_score_payload(
                answer_id, transcript, rubric_version, scores, attempt_no))

            await message.ack()

        except PermanentError as e:
            # Không retry được -> báo .NET đánh dấu Failed rồi ack (bỏ message).
            # Nếu báo Failed cũng fail (mạng) -> nack để vòng sau thử lại.
            print(f"[⛔] Lỗi vĩnh viễn answer {answer_id}: {e}")
            try:
                await post_failed(answer_id, str(e))
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
    print("[*] Kết nối RabbitMQ...")
    connection = await aio_pika.connect_robust(settings.rabbitmq_url)
    async with connection:
        channel = await connection.channel()
        await channel.set_qos(prefetch_count=1)  # chấm nặng -> xử lý 1 lúc 1 message
        queue = await declare_topology(channel)  # AI2: DLX/DLQ + queue chính (args dead-letter)
        await queue.consume(process_message)
        print(f"[✅] Worker chạy, nghe queue '{settings.queue_name}' (CTRL+C để thoát)")
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())