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

        if not answer_id or not storage_path:
            print("[❌] Thiếu answerId/audioObjectKey — bỏ message (không retry).")
            await message.ack()  # message hỏng vĩnh viễn, ack để khỏi lặp vô hạn
            return

        tmp_path = None
        try:
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
            #    score() raise ValueError khi LLM trả output không hợp lệ -> VĨNH VIỄN
            #    (scoring dùng temperature=0 nên lỗi tái lập, retry vô ích).
            #    Lỗi gọi API (rate limit/5xx/mạng) KHÔNG phải ValueError -> rơi xuống
            #    handler tạm thời -> nack để republisher thử lại sau.
            try:
                scores = await provider.score(
                    question=question_content,
                    transcript=transcript,
                    job_category=job_category,
                    criteria=criteria,
                )
            except ValueError as e:
                raise PermanentError(f"Chấm thất bại (LLM output không hợp lệ): {e}")
            print(f"[✅] Chấm xong: {scores}")

            # 4. Callback về .NET — .NET ghi transcript + answer_scores + đổi status.
            #    Lỗi gửi callback = tạm thời -> retry.
            await post_callback({
                "answerId": answer_id,
                "transcript": transcript,
                "rubricVersion": rubric_version,
                "scores": scores,  # [{criterionId, score, reasoning}, ...]
            })

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


async def main():
    print("[*] Kết nối RabbitMQ...")
    connection = await aio_pika.connect_robust(settings.rabbitmq_url)
    async with connection:
        channel = await connection.channel()
        await channel.set_qos(prefetch_count=1)  # chấm nặng -> xử lý 1 lúc 1 message
        queue = await channel.declare_queue(settings.queue_name, durable=True)
        await queue.consume(process_message)
        print(f"[✅] Worker chạy, nghe queue '{settings.queue_name}' (CTRL+C để thoát)")
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())