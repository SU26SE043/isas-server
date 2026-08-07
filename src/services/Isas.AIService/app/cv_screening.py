"""C14 — nửa AIService của pipeline sàng CV B2B (queue ``cv_screening_queue``).

Phía .NET đã đủ từ lâu (publisher + `StuckScreeningRepublisher` mỗi 2' + 2 callback endpoint)
nhưng KHÔNG có consumer nào ở đây ⇒ message chỉ chất đống. Module này là nửa còn thiếu.

Khác pipeline chấm (``worker.py``): **KHÔNG Whisper, KHÔNG tải audio/S3** — ``cvText`` nằm sẵn
trong message. Vì nhẹ hơn nhiều nên chạy **channel + consumer RIÊNG** với prefetch cao hơn:
backlog audio không được nghẽn sàng CV và ngược lại (ai.md §Pipeline sàng CV B2B).

GEN-4: KHÔNG ghi DB — kết quả trả về CampaignService qua callback ``X-Internal-Token``.
"""
import json

import aiohttp
import aio_pika

from app.config import settings
from app.providers.gemini import GeminiProvider

provider = GeminiProvider()


class PermanentCvError(Exception):
    """Lỗi KHÔNG khắc phục được bằng retry (CV rỗng, LLM trả rác tái lập).

    → báo CampaignService ``cv-failed`` (ứng viên sang ``AnalysisFailed``, HR thấy và cho chạy
    lại được) rồi ack, thay vì để message quay vòng đốt Gemini mãi.
    """


def _field(body: dict, name: str):
    """Đọc field chấp nhận CẢ camelCase LẪN PascalCase.

    ⚠ Không phải phòng thủ thừa: ``CvScreeningPublisher.cs`` gọi ``JsonSerializer.Serialize(job)``
    **không truyền options** ⇒ khoá trên dây là **PascalCase** (`CandidateId`, `CvText`…), khác hẳn
    mọi payload khác của hệ (camelCase). Nếu .NET sau này gắn `JsonNamingPolicy.CamelCase` toàn cục
    thì payload đổi sang camelCase mà KHÔNG có gì báo lỗi — đọc một kiểu là hỏng câm. Cùng mẫu
    ``body.get("transcript") or body.get("Transcript")`` ở worker.py.
    """
    if name in body:
        return body[name]
    pascal = name[0].upper() + name[1:]
    return body.get(pascal)


def parse_job(body: dict) -> dict:
    """Chuẩn hoá message .NET → dict nội bộ (tolerant casing cả ở phần tử ``criteria``)."""
    raw_criteria = _field(body, "criteria") or []
    criteria = []
    for c in raw_criteria:
        if not isinstance(c, dict):
            continue
        cid = _field(c, "criterionId")
        if cid is None:
            continue
        criteria.append({
            "criterionId": str(cid),
            "name": str(_field(c, "name") or ""),
            "description": _field(c, "description"),
            "maxScore": int(_field(c, "maxScore") or 5),
        })

    candidate_id = _field(body, "candidateId")
    callback_base = _field(body, "callbackBase")
    cv_text = _field(body, "cvText")
    return {
        "candidateId": str(candidate_id) if candidate_id else None,
        "cvText": cv_text.strip() if isinstance(cv_text, str) else None,
        "jobCategory": _field(body, "jobCategory"),
        "jdText": _field(body, "jdText"),
        "criteria": criteria,
        "callbackBase": str(callback_base).rstrip("/") if callback_base else None,
    }


def make_cv_result_payload(result: dict) -> dict:
    """Body callback ``cv-result``. ``candidateId`` nằm ở ROUTE, KHÔNG nằm trong body (DTO .NET).

    Khoá camelCase — ASP.NET bind không phân biệt hoa thường nên khớp
    ``CvResultCallbackRequest`` (`Skills`/`YearsExperience`/…).
    """
    return {
        # BK28 — tên ứng viên rút từ CV. `None` khi CV không có tên rõ ràng: .NET nhận null và
        # KHÔNG ghi đè (xem `SaveCvResultAsync`), nên gửi null an toàn hơn hẳn gửi "".
        "fullName": result.get("fullName"),
        "skills": result.get("skills") or [],
        "yearsExperience": result.get("yearsExperience"),
        "education": result.get("education") or [],
        "summary": result.get("summary"),
        "overallMatchScore": result.get("overallMatchScore") or 0,
        "criterionMatches": [
            {
                "criterionId": m["criterionId"],
                "matchScore": m["matchScore"],
                "reasoning": m.get("reasoning"),
            }
            for m in (result.get("criterionMatches") or [])
        ],
    }


async def post_cv_result(callback_base: str, candidate_id: str, payload: dict):
    """Gửi kết quả sàng về CampaignService (GEN-4 — Python KHÔNG ghi DB).

    ``callback_base`` lấy từ CHÍNH message: ``settings.dotnet_callback_base`` mặc định trỏ
    InterviewService, còn callback này phải về CampaignService.
    """
    url = f"{callback_base}/internal/campaign-candidates/{candidate_id}/cv-result"
    headers = {"X-Internal-Token": settings.internal_token}
    async with aiohttp.ClientSession() as session:
        async with session.post(url, json=payload, headers=headers) as resp:
            if resp.status >= 300:
                text = await resp.text()
                raise RuntimeError(f"Callback cv-result fail {resp.status}: {text}")
            print(f"[🎉] Callback Campaign OK cho candidate {candidate_id}")


async def post_cv_failed(callback_base: str, candidate_id: str, reason: str):
    """Báo CampaignService đánh dấu ``AnalysisFailed`` để ứng viên thoát kẹt ``Analyzing``."""
    url = f"{callback_base}/internal/campaign-candidates/{candidate_id}/cv-failed"
    headers = {"X-Internal-Token": settings.internal_token}
    async with aiohttp.ClientSession() as session:
        async with session.post(url, json={"reason": reason}, headers=headers) as resp:
            if resp.status >= 300:
                text = await resp.text()
                raise RuntimeError(f"Callback cv-failed fail {resp.status}: {text}")
            print(f"[⛔] Đã báo Campaign AnalysisFailed cho candidate {candidate_id}")


async def process_cv_message(message: aio_pika.IncomingMessage):
    """Ack/nack THỦ CÔNG (như worker.py) — không dùng ``message.process()`` vốn ack ngay.

    Phân loại lỗi:
      • body hỏng / thiếu candidateId / thiếu callbackBase → **ack + log**: không có đường nào
        báo về .NET, giữ lại chỉ làm poison queue.
      • CV rỗng, LLM trả rác sau ``score_max_attempts`` lần → **cv-failed + ack** (lỗi vĩnh viễn).
      • Gemini 5xx/timeout/mạng → **nack(requeue=False)**; ``StuckScreeningRepublisher`` (.NET,
        quét mỗi 2', ``Analyzing`` > 15') sẽ đẩy bản MỚI. Cố ý KHÔNG ``requeue=True``: queue này
        không có DLX, redeliver ngay lập tức sẽ quay vòng NÓNG đúng lúc Gemini đang rate-limit —
        tức là đốt quota nhanh nhất đúng lúc không nên. Giống hệt quyết định ở worker.py.
    """
    try:
        body = json.loads(message.body.decode())
        job = parse_job(body)
    except Exception as e:
        print(f"[❌] Message sàng CV không đọc được: {e}")
        await message.ack()
        return

    candidate_id = job["candidateId"]
    callback_base = job["callbackBase"]
    if not candidate_id or not callback_base:
        print("[❌] Thiếu candidateId/callbackBase — bỏ message (không có đường báo về .NET).")
        await message.ack()
        return

    try:
        if not job["cvText"]:
            raise PermanentCvError("cvText rỗng — không parse được nội dung CV")
        if not job["criteria"]:
            # Không có tiêu chí thì không có gì để chấm; .NET cũng không lưu được điểm nào.
            raise PermanentCvError("criteria rỗng — không có tiêu chí campaign để chấm khớp")

        # AI3 — lỗi parse LLM thường chợp nhoáng → thử lại vài lần trước khi bó tay,
        # y hệt vòng retry của score() trong worker.py.
        result = None
        for attempt in range(1, settings.score_max_attempts + 1):
            try:
                result = await provider.analyze_cv(
                    job["cvText"], job["jdText"], job["jobCategory"], job["criteria"])
                break
            except ValueError as e:
                if attempt >= settings.score_max_attempts:
                    raise PermanentCvError(
                        f"Sàng CV thất bại sau {settings.score_max_attempts} lần "
                        f"(LLM output không hợp lệ): {e}")
                print(f"[↻] Sàng CV lỗi parse lần {attempt}/"
                      f"{settings.score_max_attempts} (thử lại): {e}")

        await post_cv_result(callback_base, candidate_id, make_cv_result_payload(result))
        await message.ack()

    except PermanentCvError as e:
        print(f"[⛔] Lỗi vĩnh viễn khi sàng CV candidate {candidate_id}: {e}")
        try:
            await post_cv_failed(callback_base, candidate_id, str(e))
            await message.ack()
        except Exception as report_err:
            # Báo Failed cũng hỏng (mạng) → nack để vòng sau thử lại, đừng nuốt mất.
            print(f"[❌] Báo cv-failed không được: {report_err} -> nack")
            await message.nack(requeue=False)

    except Exception as e:
        print(f"[⚠️] Lỗi tạm thời khi sàng CV candidate {candidate_id}: {e} -> nack (republish sau)")
        await message.nack(requeue=False)


async def declare_cv_topology(channel):
    """Khai ``cv_screening_queue`` KHỚP Y HỆT ``CvScreeningPublisher.cs``.

    Publisher khai `durable: true, exclusive: false, autoDelete: false, arguments: null` ⇒ ở đây
    **KHÔNG được** truyền ``arguments`` (kể cả DLX): hai bên redeclare cùng queue với arguments
    khác nhau → RabbitMQ ném PRECONDITION_FAILED 406 và consumer không bao giờ lên được.
    """
    return await channel.declare_queue(settings.cv_screening_queue_name, durable=True)


async def maybe_start_cv_screening_consumer(connection) -> bool:
    """Cổng kill-switch — trả ``True`` nếu đã bật consumer, ``False`` nếu cờ tắt.

    VÌ SAO LÀ HÀM RIÊNG chứ không phải ``if`` trong ``worker.main()``: ``main()`` mở connection thật
    tới RabbitMQ nên không unit-test được, mà nhánh này lại là **lớp an toàn tiền bạc** — lúc consumer
    ra đời, queue đang tồn 713 message của đúng 8 ứng viên, bật nhầm là 713 lượt Gemini. Guard nằm
    trong ``main()`` thì gỡ nó đi cũng **không test nào đỏ** (đã đo). Tách ra để cờ tắt kiểm được.
    """
    if not settings.cv_screening_enabled:
        print("[⏸] Consumer sàng CV TẮT (cv_screening_enabled=false)")
        return False
    await start_cv_screening_consumer(connection)
    return True


async def start_cv_screening_consumer(connection):
    """Mở channel RIÊNG trên CÙNG connection/tiến trình worker rồi consume.

    Channel riêng để ``set_qos`` riêng: scoring giữ ``prefetch=1`` (chấm nặng, có Whisper), sàng CV
    chạy ``cv_screening_prefetch`` (nhẹ). Dùng chung channel là mất luôn khác biệt đó.
    """
    channel = await connection.channel()
    await channel.set_qos(prefetch_count=settings.cv_screening_prefetch)
    queue = await declare_cv_topology(channel)
    await queue.consume(process_cv_message)
    print(f"[✅] Consumer sàng CV nghe queue '{settings.cv_screening_queue_name}' "
          f"(prefetch={settings.cv_screening_prefetch})")
    return queue
