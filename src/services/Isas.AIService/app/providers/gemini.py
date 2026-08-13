import json
import logging
from typing import NamedTuple

from google import genai
from google.genai import types

from app.config import settings
from app.resources import sanitize_resources, count_rejected_urls
from app.lesson_quality import (
    evaluate_lesson_theory, message as lesson_message, render_lesson_markdown,
)
from app.question_quality import coverage_defects, verify_defect
from app import prompt_registry
from app.prompts import (
    build_prompt, build_scoring_prompt, build_criteria_prompt,
    build_criterion_levels_prompt,
    build_cv_analysis_prompt, build_repo_analysis_prompt,
    build_roadmap_prompt, build_lesson_theory_prompt, build_summarize_roadmap_prompt,
    build_summarize_session_prompt, build_decide_next_prompt,
    build_verify_questions_prompt,
)
from app.providers.base import QuestionProvider
from app.usage import report_usage

logger = logging.getLogger(__name__)


# ── Q16: câu đào sâu bị CỤT ────────────────────────────────────────────────────
# Trên deploy 2026-08-07, `practice_questions` có một câu `Clarify` dài 31 ký tự —
# "Bạn có thể giải thích rõ hơn về" — đã trả cho ứng viên, ứng viên đã trả lời, answer đã `Scored`
# (các câu Clarify khác 168/177 ký tự, hoàn chỉnh).
#
# Dấu câu kết. Cố ý gồm cả `.`/`!` chứ không chỉ `?`: câu mệnh lệnh ("Hãy mô tả cách bạn xử lý lỗi.")
# là câu hỏi hợp lệ, ép dấu `?` sẽ bắt oan đúng nhóm câu tự nhiên nhất.
_QUESTION_TERMINATORS = "?.!…。？！"

# E9b — độ dài tối thiểu của một descriptor mốc.
#
# KHÔNG phải để đo chất lượng — ngưỡng ký tự không đo được cái đó. Nó chặn đúng một hình dạng đầu
# ra hỏng: model trả lại chính nhãn điểm ("Mức 3/5", "Khá", "Đạt"). Đó là thứ dải mặc định đang in
# ra, tức là nếu để lọt thì tính năng vừa xây trả về đúng cái nó sinh ra để thay thế, và HR không
# có cách nào nhận ra vì màn hình vẫn có chữ.
LEVEL_DESCRIPTOR_MIN_CHARS = 20


def _looks_truncated(question: str) -> bool:
    """Câu hỏi có bị cắt giữa chừng không? CỐ Ý không đo bằng ngưỡng ĐỘ DÀI.

    Ngưỡng độ dài đo sai thứ cần đo: một câu hỏi ngắn vẫn có thể trọn vẹn ("Bạn dùng index nào?"),
    còn một câu dài vẫn có thể cụt. Thay vào đó dùng đúng thủ pháp repo đã dùng ở chỗ khác — nêu
    hợp đồng trong prompt rồi KIỂM BẰNG MÁY (model chỉ cite được chunkId đã cấp D27; `criterion`
    phải trùng nguyên văn tên tiêu chí BC13; url phải thuộc allowlist F15). Ở đây hợp đồng là:
    câu hỏi kết thúc bằng dấu câu.

    Hai dấu hiệu, cả hai đều về TÍNH TRỌN VẸN:
      1. không còn chữ/số nào sau khi bỏ dấu câu → mô hình chép lại placeholder ("...", "?"),
         mà `if not next_q` không bắt được vì chuỗi khác rỗng;
      2. ký tự cuối không phải dấu kết câu → dấu vết kinh điển của một câu bị cắt.

    Bắt oan không phải là không có giá: nó tốn thêm một lượt sinh, và cạn lượt thì thành 502 (.NET
    degrade về luồng tĩnh — answer VẪN được lưu). Đổi lại là không đưa nửa câu cho ứng viên trả lời
    rồi đem đi chấm. Đánh đổi nghiêng hẳn về phía chặn.
    """
    text = question.strip()
    if not any(ch.isalnum() for ch in text):
        return True
    return text[-1] not in _QUESTION_TERMINATORS


def _generation_diagnostics(response) -> str:
    """Vì sao lượt sinh này hỏng — dữ liệu để CHỐT nguyên nhân Q16 bằng số thật ở lớp 3.

    KHÔNG BAO GIỜ raise (idiom ``app.usage.extract_usage``): đây là dòng log phụ, làm hỏng đường
    chính vì nó thì mất nhiều hơn được. Đọc bằng ``getattr`` vì test double dựng response bằng
    ``type("R", (), {...})()`` nên không có ``candidates``.

    ``finish_reason=MAX_TOKENS`` ở đây sẽ chứng minh giả thuyết "bị cắt lúc truyền"; còn
    ``STOP`` kèm ``candidates_token_count`` nhỏ nghĩa là chính mô hình tự đóng chuỗi — hai nguyên
    nhân đó cần hai cách sửa khác hẳn nhau, nên phải phân biệt được thay vì đoán.
    """
    try:
        candidates = getattr(response, "candidates", None) or []
        finish = getattr(candidates[0], "finish_reason", None) if candidates else None
        meta = getattr(response, "usage_metadata", None)
        out_tokens = getattr(meta, "candidates_token_count", None) if meta is not None else None
        return f"finish_reason={finish!r} candidates_token_count={out_tokens!r}"
    except Exception:  # noqa: BLE001 — xem docstring: đo không được làm hỏng đường chính
        return "finish_reason=? candidates_token_count=? (không đọc được)"


class ScoreOutcome(NamedTuple):
    """Kết quả 1 lượt chấm (F13).

    ``score()`` TRƯỚC ĐÂY trả thẳng ``list[dict]``; nay trả kèm ``sample_answer`` nên phải
    đổi shape. CỐ Ý dùng NamedTuple thay vì thêm biến thể trả về theo cờ: kiểu trả về hợp
    nhất, và call site cũ (``result[0]["criterionId"]``) VỠ TO ngay lần chạy đầu thay vì
    âm thầm đọc nhầm phần tử.

    sample_answer: câu trả lời mẫu mức tối đa cho ĐÚNG câu hỏi vừa chấm; None khi LLM
    không trả (phần phụ trợ, không đánh hỏng lượt chấm).

    prompt_version (BK23): con dấu phiên bản của bộ mảnh prompt ĐÃ THỰC SỰ dựng nên lượt chấm
      này — đọc ngay sau ``refresh_if_stale()`` bên trong chính ``score()``, nên nó là phiên bản
      của prompt vừa gửi đi, không phải phiên bản "đang nằm trong DB ở một thời điểm khác".
      0 = thuần mặc định (chưa ai tuỳ biến, hoặc registry tắt/chưa nạp được).

      Vì sao đọc TRONG ``score()`` chứ không để worker đọc sau khi ``score()`` trả về: cache là
      biến module toàn cục và AI3 retry gọi lại ``score()`` (mỗi lần lại ``refresh_if_stale``),
      nên đọc ở ngoài có thể lấy phải phiên bản của một lượt refresh KHÁC với lượt đã dựng prompt.
      Chụp tại chỗ thì con dấu và prompt luôn cùng một lượt.

      Mặc định 0 để mọi call site cũ dựng ScoreOutcome 2 trường (test) chạy nguyên.
    """
    scores: list[dict]
    sample_answer: str | None
    prompt_version: int = 0


class QuestionGenerationResult(NamedTuple):
    """Kết quả sinh câu hỏi (RAG grounding, Contract 2).

    ``generate()`` TRƯỚC ĐÂY trả thẳng ``list[str]``; nay trả kèm ``citations`` nên đổi shape —
    theo mẫu ``ScoreOutcome`` (F13): kiểu trả về HỢP NHẤT, call site cũ dùng ``len(result)`` vỡ TO
    ngay lần chạy đầu thay vì âm thầm đọc nhầm.

    ``citations``: per-question ``[{questionIndex, citedChunkIds}]``, chỉ có khi request cấp
    grounding (mỗi ``citedChunkIds`` ⊆ tập đã cấp — provider đã drop id lạ). ``None`` khi ungrounded
    → endpoint không trả field citations (giữ shape cũ cho Campaign B2B).

    ``target_criteria``: mảng SONG SONG index-aligned với ``questions`` — phần tử i = criterionId
    mà câu i nhắm tới (⊆ tập ``criteria`` đã cấp). ``None`` khi request không cấp ``criteria``
    → endpoint không trả field targetCriteria (shape cũ nguyên vẹn). Trường thứ ba có mặc định nên
    mọi call site cũ dựng 1-2 trường (kể cả test double) chạy nguyên."""
    questions: list[str]
    citations: list[dict] | None = None
    target_criteria: list[list[str]] | None = None


class LessonTheoryResult(NamedTuple):
    """Kết quả sinh lý thuyết bài học (RAG grounding, Contract 2).

    ``generate_lesson_theory()`` TRƯỚC ĐÂY trả ``(theory, resources)``; nay thêm
    ``cited_chunk_ids`` (danh sách phẳng ⊆ tập grounding đã cấp) → 3 trường, call site cũ
    ``theory, resources = ...`` vỡ TO ngay lần chạy đầu (mẫu F13). ``cited_chunk_ids`` = None khi
    ungrounded → endpoint không trả field citedChunkIds (giữ shape cũ)."""
    theory: str
    resources: list[dict]
    cited_chunk_ids: list[str] | None = None


def _keep_known_ids(raw, allowed: set[str]) -> list[str]:
    """Giữ lại id ⊆ ``allowed``, bỏ trùng, giữ thứ tự. DROP mọi thứ khác.

    Chống bịa BY-CONSTRUCTION: prompt có dặn model "chỉ dùng id đã cấp" nhưng đó mới là lớp phòng
    thủ thứ nhất — lớp thứ hai là ở đây, không tin lời hứa của model. Dùng chung cho ``citedChunkIds``
    (grounding, D27) và ``targetCriterionIds`` (chấm-theo-phạm-vi): hai hợp đồng khác nhau nhưng
    cùng một luật lọc, tách ra để lần siết sau không phải sửa hai chỗ rồi quên một.
    """
    if not isinstance(raw, list):
        return []
    kept = (x.strip() for x in raw if isinstance(x, str) and x.strip() in allowed)
    return list(dict.fromkeys(kept))


def _question_text(item) -> str:
    """Text câu hỏi từ output model, chấp nhận CẢ HAI hình dạng (chuỗi trần / object có ``text``).

    Model đôi khi lờ ``response_schema``; cả hai chiều lờ đều phải nhận được, vì thứ rơi ra khi
    không nhận là một CÂU HỎI RÁC gửi thẳng cho ứng viên đã trả credit — không lỗi nào nổ.
    """
    return str(item.get("text", "") if isinstance(item, dict) else item).strip()


class GeminiProvider(QuestionProvider):
    def __init__(self) -> None:
        self._client = genai.Client(api_key=settings.gemini_api_key)

    # ── RAG GROUNDING (Contract 1): SINH VECTOR, KHÔNG ghi kho nào (GEN-4) ──────
    async def embed(self, texts: list[str], task_type: str) -> list[list[float]]:
        """Sinh embedding cho batch text — InterviewService gọi lúc ingest / truy hồi.

        KHÔNG đi qua chokepoint F22 (``_generate``): đó là cho ``generate_content`` (đo token
        chấm/sinh, đơn giá model chat). ``embed_content`` là API + bảng giá KHÁC → đo riêng
        (Phase sau nếu cần), không nhét nhầm vào thống kê model chat.

        Trả về list vector cùng thứ tự ``texts``; ``output_dimensionality`` cắt về 768 (Matryoshka)
        khớp collection Qdrant ``knowledge``.
        """
        resp = await self._client.aio.models.embed_content(
            model=settings.embed_model,
            contents=texts,
            config=types.EmbedContentConfig(
                output_dimensionality=settings.embed_dim,
                task_type=task_type,
            ),
        )
        return [list(e.values or []) for e in (resp.embeddings or [])]

    async def _verify_question_knowledge(self, questions: list[str], grounding: list[dict] | None,
                                         language: str = "vi") -> tuple[list[str], list[dict] | None]:
        """QV1 best-effort: only a concrete contradiction is a defect; retrieval miss is valid."""
        if not grounding:
            return [], None
        # Registry là no-op ở đây (prompt kiểm chứng HARDCODE, F21 không có khe nào cho nó) — nhưng
        # guard cấu trúc `test_moi_ham_dung_build_prompt_deu_phai_nap_registry` cố ý KHÔNG có ngoại
        # lệ, và một guard có ngoại lệ là guard sẽ bị lách. Cache đã ấm từ `generate()` nên không
        # thêm lượt gọi mạng nào.
        await prompt_registry.refresh_if_stale()
        prompt = build_verify_questions_prompt(questions, grounding)
        response = await self._generate("verify_questions", contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0,
                response_mime_type="application/json",
                # Thiếu schema thì "trả JSON đúng dạng" chỉ còn là lời dặn trong prompt — đúng thứ
                # mà một chunk độc nhắm vào đầu tiên. Mọi lượt gọi JSON khác của file này đều khai
                # schema; lượt này từng là ngoại lệ duy nhất.
                response_schema={
                    "type": "object",
                    "properties": {
                        "checks": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "questionIndex": {"type": "integer"},
                                    "citedChunkIds": {"type": "array", "items": {"type": "string"}},
                                    "reason": {"type": "string", "nullable": True},
                                },
                                "required": ["questionIndex"],
                            },
                        }
                    },
                    "required": ["checks"],
                },
            ))
        data = json.loads((response.text or "").strip())
        allowed = {str(g.get("chunkId")).strip() for g in grounding if g.get("chunkId")}
        checks = data.get("checks", [])
        citations = [[] for _ in questions]
        defects: list[str] = []
        for item in checks if isinstance(checks, list) else []:
            if not isinstance(item, dict) or not isinstance(item.get("questionIndex"), int):
                continue
            index = item["questionIndex"]
            if 0 <= index < len(questions):
                citations[index] = _keep_known_ids(item.get("citedChunkIds"), allowed)
                reason = str(item.get("reason") or "").strip()
                if reason:
                    # KHÔNG nhét `reason` nguyên văn: nó đi thẳng vào prompt lượt SINH.
                    defects.append(verify_defect(index, reason, language))
        return defects, [{"questionIndex": i, "citedChunkIds": ids} for i, ids in enumerate(citations)]

    # ── F22 (FR18): CHOKEPOINT DUY NHẤT cho mọi lượt gọi Gemini ────────────────
    async def _generate(self, operation: str, *, contents, config,
                        model: str | None = None, defer_report: bool = False):
        """Bọc ``generate_content`` để ĐO token/chi phí (F22).

        MỌI lượt gọi Gemini của service đi qua đây — cố ý một cửa thay vì rải
        ``usage_metadata`` ra 10 chỗ: rải thì lần thêm endpoint thứ 11 sẽ quên,
        và "quên đo" là loại lỗi không ai phát hiện ra (không có gì hỏng cả, chỉ
        là con số thiếu thầm lặng).

        Ghi nhận NGAY sau khi có response, TRƯỚC khi caller parse: token đã bị
        đốt rồi kể cả khi output malformed — mà đó lại đúng là những lượt ĐẮT
        nhất (AI3 retry tới ``score_max_attempts`` lần). Hoãn ghi tới sau parse
        sẽ làm mất đúng phần chi phí ta cần thấy nhất.

        ``defer_report=True``: caller tự gọi ``report_usage`` (chỉ lesson-theory
        dùng, để đính kèm số liệu URL bị loại) — caller đó BẮT BUỘC dùng
        try/finally, xem :meth:`generate_lesson_theory`.

        ``operation`` = nhãn đường gọi → admin xem tiêu thụ THEO ENDPOINT.
        """
        used_model = model or settings.gemini_model
        response = await self._client.aio.models.generate_content(
            model=used_model, contents=contents, config=config)
        if not defer_report:
            await report_usage(operation, used_model, response)
        return response

    async def generate(self, job_category: str, cv_text: str | None,
                       jd_text: str | None, count: int | None = None,
                       focus_criteria: list[str] | None = None,
                       grounding: list[dict] | None = None,
                       criteria: list[dict] | None = None,
                       language: str = "vi", seniority: str | None = None,
                       _retry_feedback: list[str] | None = None,
                       _attempt: int = 1) -> QuestionGenerationResult:
        """Sinh câu hỏi. ``criteria`` = tiêu chí NỘI DUNG ``[{criterionId, name}]`` để gắn nhãn
        phạm vi đánh giá cho từng câu (chấm-theo-phạm-vi); vắng ⇒ prompt/schema/kết quả GIỮ
        NGUYÊN XI như trước (mẫu ``criteria`` của C14 ở :meth:`analyze_cv`).

        ``seniority`` (SEN1) = cấp độ ứng viên → hiệu chỉnh độ khó bộ câu gốc; ``None`` ⇒ prompt
        không đổi một chữ."""
        # F2b — số câu do caller quyết định; settings.question_count chỉ còn là MẶC ĐỊNH khi không gửi.
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        effective_count = count if count is not None else settings.question_count
        # QV1: grounding vẫn đi trên dây để citations/verify dùng được, nhưng không neo lượt SINH.
        prompt_grounding = None if settings.question_verify_enabled else grounding
        prompt = build_prompt(job_category, cv_text, jd_text, effective_count,
                              focus_criteria, prompt_grounding, criteria, _retry_feedback,
                              language=language, seniority=seniority)

        # RAG grounding — có grounding ⇒ mỗi câu hỏi kèm citedChunkIds (Contract CITATION).
        # Chấm-theo-phạm-vi — có criteria ⇒ kèm targetCriterionIds.
        # KHÔNG cái nào ⇒ giữ nguyên schema cũ {questions:[str]} → mọi caller cũ không đổi.
        #
        # ⚠ Phải bám `prompt_grounding`, KHÔNG phải `grounding`: khi QV1 bật, lượt sinh KHÔNG được cấp
        # tài liệu, nên bảo model trả `citedChunkIds` là đòi nó trích cái nó chưa từng thấy. Dùng
        # `grounding` ở đây làm prompt (bảo trả CHUỖI TRẦN) chọi thẳng với schema (ép OBJECT) — hai vế
        # của cùng một hợp đồng nói ngược nhau, và citations sinh ra khi đó chỉ toàn rỗng.
        grounded = bool(prompt_grounding)
        labeled = bool(criteria)
        if grounded or labeled:
            item_properties: dict = {"text": {"type": "string"}}
            if grounded:
                item_properties["citedChunkIds"] = {"type": "array", "items": {"type": "string"}}
            if labeled:
                item_properties["targetCriterionIds"] = {"type": "array", "items": {"type": "string"}}
            question_schema = {
                "type": "object",
                "properties": item_properties,
                # CỐ Ý chỉ `text` là bắt buộc: ép `targetCriterionIds` vào `required` là ép model
                # phải điền một mảng cho MỌI câu, mà rỗng lại là câu trả lời hợp lệ (câu hỏi không
                # nhắm tiêu chí nội dung nào) ⇒ ép sẽ đẩy model sang gắn bừa — đúng thứ đang chống.
                "required": ["text"],
            }
        else:
            question_schema = {"type": "string"}

        response = await self._generate(
            "generate_questions",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.7,
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "questions": {
                            "type": "array",
                            "items": question_schema,
                        }
                    },
                    "required": ["questions"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM trả về JSON không hợp lệ: {text[:200]}")

        raw = data.get("questions", [])
        if not isinstance(raw, list) or not raw:
            raise ValueError("LLM không trả về danh sách câu hỏi hợp lệ.")

        if not grounded and not labeled:
            # `_question_text` chứ không `str(q)`: model lờ schema mà trả OBJECT thì `str(dict)` biến
            # nguyên cái repr Python thành CÂU HỎI gửi cho ứng viên. Đây là vế đối xứng của phòng thủ
            # đã có ở nhánh dưới ("model lờ schema, trả chuỗi trần"), và nay mới với tới được: QV1 bật
            # thì buổi CÓ grounding cũng đi qua nhánh này (lượt sinh cố ý ungrounded).
            questions = [t for q in raw if (t := _question_text(q))]
            if not questions:
                raise ValueError("LLM trả về danh sách câu hỏi rỗng sau khi lọc.")
            # ⚠ KHÔNG return sớm ở đây. Nhánh này CÓ THỂ là buổi grounded đang bật QV1 (lượt sinh
            # ungrounded có chủ đích) — return sớm thì cổng kiểm chứng bị nhảy qua IM LẶNG và
            # citations không bao giờ được điền.
            result = QuestionGenerationResult(questions=questions[:effective_count], citations=None)
            return await self._finish(
                result, criteria, grounding, language, effective_count,
                job_category, cv_text, jd_text, count, focus_criteria, seniority, _attempt)

        # Có grounding và/hoặc criteria — tách text + lọc id. DROP mọi id KHÔNG thuộc tập đã cấp
        # (chống bịa by-construction — không tin lời hứa của model): id lạ = model tự phịa.
        allowed_chunks = ({str(g.get("chunkId")).strip() for g in prompt_grounding
                           if g.get("chunkId")} if grounded else set())
        allowed_criteria = ({str(c.get("criterionId")).strip() for c in criteria
                             if c.get("criterionId")} if labeled else set())
        questions: list[str] = []
        cited_lists: list[list[str]] = []
        target_lists: list[list[str]] = []
        for item in raw:
            if isinstance(item, dict):
                q_text = _question_text(item)
                cited_raw = item.get("citedChunkIds") or []
                target_raw = item.get("targetCriterionIds") or []
            else:
                # Model lờ schema, trả chuỗi trần → vẫn nhận câu hỏi, coi như không cite/không nhãn.
                q_text = str(item).strip()
                cited_raw = []
                target_raw = []
            if not q_text:
                continue
            questions.append(q_text)
            cited_lists.append(_keep_known_ids(cited_raw, allowed_chunks))
            # FAIL-OPEN CÓ CHỦ ĐÍCH — thiếu nhãn / nhãn toàn id lạ ⇒ [] chứ KHÔNG raise (khác
            # `criterionMatches` của C14, chỗ đó raise là đúng vì nó LÀ kết quả sàng lọc).
            # Ở đây sinh câu hỏi nằm trên đường tạo buổi luyện ĐÃ RESERVE CREDIT (PAY-5): biến
            # một cái nhãn phụ thành đường làm hỏng cả buổi thì đắt hơn nhiều so với việc thiếu
            # nhãn — .NET nhận [] và tự xử (mẫu `fullName` của BK28 cố ý không raise).
            target_lists.append(_keep_known_ids(target_raw, allowed_criteria))

        if not questions:
            raise ValueError("LLM trả về danh sách câu hỏi rỗng sau khi lọc.")

        questions = questions[:effective_count]
        cited_lists = cited_lists[:effective_count]
        target_lists = target_lists[:effective_count]
        citations = ([{"questionIndex": i, "citedChunkIds": cited_lists[i]}
                      for i in range(len(questions))] if grounded else None)
        # Mảng song song: LUÔN cùng độ dài `questions` (cắt cùng một lát ở trên) — .NET zip theo
        # index nên lệch độ dài là gán nhãn của câu này cho câu khác.
        result = QuestionGenerationResult(questions=questions, citations=citations,
                                          target_criteria=target_lists if labeled else None)
        return await self._finish(
            result, criteria, grounding, language, effective_count,
            job_category, cv_text, jd_text, count, focus_criteria, seniority, _attempt)

    async def _finish(self, result: QuestionGenerationResult, criteria: list[dict] | None,
                      grounding: list[dict] | None, language: str, effective_count: int,
                      job_category: str, cv_text: str | None, jd_text: str | None,
                      count: int | None, focus_criteria: list[str] | None,
                      seniority: str | None,
                      _attempt: int) -> QuestionGenerationResult:
        """Vòng chất lượng (SC1c) + cổng kiểm chứng (QV1), CHUNG cho mọi nhánh của :meth:`generate`.

        Tách ra vì `generate` có hai đường về (chuỗi trần / object) và trước đây đường chuỗi trần
        return SỚM ⇒ buổi grounded bật QV1 (lượt sinh cố ý ungrounded, model trả chuỗi trần) nhảy
        qua cả kiểm chứng lẫn citations mà không lỗi gì.
        """
        # SC1c fail-open: only retry the complete set once; remaining defects still deliver a session.
        # `effective_count` và `language` PHẢI truyền: thiếu count thì bản kiểm đòi phủ 100% ngay cả khi
        # chính prompt đã bảo model "chỉ chọn {count} tiêu chí khác nhau" ⇒ đốt một lượt Gemini TẤT ĐỊNH;
        # thiếu language thì buổi tiếng Anh nhận nhận xét sửa bài bằng tiếng Việt (Q10).
        defects = coverage_defects(result.target_criteria, criteria, effective_count,
                                   language=language)
        if settings.question_verify_enabled:
            try:
                knowledge_defects, verified_citations = await self._verify_question_knowledge(
                    result.questions, grounding, language)
                defects.extend(knowledge_defects)
                # CHỈ ghi đè khi kiểm chứng CHẠY XONG. Lượt kiểm hỏng thì `citations` giữ nguyên giá
                # trị của lượt sinh — mà ở chế độ QV1 giá trị đó là None ⇒ `response_model_exclude_none`
                # bỏ hẳn field ⇒ .NET thấy "KHÔNG CÓ citation" thay vì "đã kiểm và không tìm ra nguồn
                # nào". Hai thứ đó khác nhau, và trả rỗng-mà-trông-như-đã-kiểm là nói dối đúng chỗ D27
                # cấm (ungrounded thì nhận ungrounded, KHÔNG dựng citation giả).
                if verified_citations is not None:
                    result = QuestionGenerationResult(result.questions, verified_citations, result.target_criteria)
            except Exception:  # verification is auxiliary; never fail a paid session
                logger.exception("QV1 verification failed; delivering questions without citations")
        if defects and _attempt < max(1, settings.question_max_attempts):
            # ⚠ Đuôi truyền bằng TỪ KHOÁ, không positional. Trước SEN1 dòng này là
            # `..., language, defects, _attempt + 1)` — chèn thêm một tham số vào giữa `generate`
            # sẽ khiến `defects` lặng lẽ rơi vào ô tham số mới: lượt viết lại vẫn chạy, vẫn 200,
            # chỉ là mất sạch nhận xét sửa bài. Không lỗi nào nổ.
            return await self.generate(job_category, cv_text, jd_text, count, focus_criteria,
                                       grounding, criteria, language, seniority,
                                       _retry_feedback=defects, _attempt=_attempt + 1)
        return result

    async def suggest_criteria(self, job_category: str, jd_text: str | None,
                               criteria_text: str | None, count: int, language: str = "vi") -> list[dict]:
        """Đề xuất bộ tiêu chí CÓ CẤU TRÚC (C8) — weight chuẩn hoá tổng = 1."""
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_criteria_prompt(job_category, jd_text, criteria_text, count, language=language)
        response = await self._generate(
            "suggest_criteria",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.4,
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "criteria": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "name": {"type": "string"},
                                    "description": {"type": "string"},
                                    "weight": {"type": "number"},
                                    "maxScore": {"type": "integer"},
                                },
                                "required": ["name", "weight"],
                            },
                        }
                    },
                    "required": ["criteria"],
                },
            ),
        )
        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM trả JSON không hợp lệ: {text[:200]}")

        items = [c for c in data.get("criteria", []) if isinstance(c, dict) and c.get("name")]
        if not items:
            raise ValueError("LLM không trả tiêu chí hợp lệ.")

        # chuẩn hoá weight về tổng = 1
        total = sum(float(c.get("weight", 0) or 0) for c in items) or 1.0
        for c in items:
            c["weight"] = round(float(c.get("weight", 0) or 0) / total, 4)
            c["maxScore"] = int(c.get("maxScore", 5) or 5)
            c["description"] = c.get("description")
        return items

    async def suggest_criterion_levels(self, job_category: str, criteria: list[dict],
                                       jd_text: str | None = None,
                                       level_count: int | None = None,
                                       language: str = "vi",
                                       seniority: str | None = None) -> list[dict]:
        """E9b — đề xuất MỐC ĐIỂM cho từng tiêu chí campaign. KHÔNG ghi DB (GEN-4).

        Trả ``[{"criterionId": str, "levels": [{"score": int, "descriptor": str}]}]`` theo ĐÚNG
        thứ tự ``criteria`` đầu vào; mỗi ``levels`` đã sort tăng, bỏ trùng score, và ⊆ [0, maxScore].

        🛑 **KHÔNG fallback dải mặc định.** Đường ``/suggest-criteria`` để .NET nuốt lỗi rồi dựng
        bộ tiêu chí cứng — chấp nhận được ở đó vì tiêu chí mặc định là *gợi ý* mà HR nhìn thấy và
        sửa. Ở đây thì khác: HR sẽ đọc ``Mức 3/10`` và tin đó là mốc AI viết ra, rồi phát link đi
        tuyển bằng một thang không ai soạn. Không có mốc là TRẠNG THÁI HỢP LỆ (hôm nay 100%
        campaign đang như vậy), nên fail-loud không chặn ai — nó chỉ từ chối nói dối.
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()

        # maxScore là RÀNG BUỘC của từng tiêu chí, không phải hằng chung: hai tiêu chí trong cùng
        # campaign hoàn toàn có thang khác nhau (thang 5 và thang 10).
        max_by_id: dict[str, int] = {}
        for c in criteria:
            cid = str(c.get("criterionId") or c.get("CriterionId") or "").strip()
            if not cid:
                continue
            max_by_id[cid] = int(c.get("maxScore") or c.get("MaxScore") or 5)
        if not max_by_id:
            raise ValueError("Không có tiêu chí hợp lệ để sinh mốc điểm.")

        prompt = build_criterion_levels_prompt(
            job_category, criteria, jd_text, level_count,
            language=language, seniority=seniority)

        response = await self._generate(
            "suggest_criterion_levels",
            contents=prompt,
            config=types.GenerateContentConfig(
                # Thấp như `suggest_criteria`: đây là văn bản định nghĩa THƯỚC ĐO, không phải chỗ
                # cần sáng tạo — dao động ở đây làm hai lần bấm "AI gợi ý" ra hai thang khác nhau.
                temperature=0.3,
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "criteria": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "criterionId": {"type": "string"},
                                    "levels": {
                                        "type": "array",
                                        "items": {
                                            "type": "object",
                                            "properties": {
                                                "score": {"type": "integer"},
                                                "descriptor": {"type": "string"},
                                            },
                                            "required": ["score", "descriptor"],
                                        },
                                    },
                                },
                                "required": ["criterionId", "levels"],
                            },
                        }
                    },
                    "required": ["criteria"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM trả JSON không hợp lệ: {text[:200]}")

        levels_by_id: dict[str, list[dict]] = {}
        for item in data.get("criteria", []):
            if not isinstance(item, dict):
                continue
            cid = str(item.get("criterionId", "")).strip()
            # AI-3 — id không thuộc tập đã cấp là id BỊA (mẫu `citedChunkId` D27 / `criterionId`
            # C14): drop, KHÔNG cố đoán xem model định nói tiêu chí nào.
            if cid not in max_by_id or cid in levels_by_id:
                continue

            mx = max_by_id[cid]
            by_score: dict[int, str] = {}
            for lv in (item.get("levels") or []):
                if not isinstance(lv, dict):
                    continue
                raw = lv.get("score")
                # `bool` là con của `int` trong Python — `True` sẽ lọt thành score 1 nếu không loại.
                if isinstance(raw, bool) or not isinstance(raw, (int, float)):
                    continue
                s = int(raw)
                if s < 0 or s > mx:
                    continue          # ngoài thang của CHÍNH tiêu chí này → bỏ
                if s in by_score:
                    continue          # trùng score → giữ mốc đầu tiên
                by_score[s] = str(lv.get("descriptor") or "").strip()

            # Hai mốc trùng score làm cả `min(valid_levels, key=…)` phía provider lẫn `ResolveLevel`
            # phía C# snap KHÔNG XÁC ĐỊNH ⇒ E9 sai âm thầm. Sort + dedupe ở đây là bất biến đầu ra.
            levels_by_id[cid] = [{"score": s, "descriptor": by_score[s]}
                                 for s in sorted(by_score)]

            if len(levels_by_id[cid]) < 2:
                raise ValueError(
                    f"Tiêu chí {cid}: chỉ có {len(levels_by_id[cid])} mốc hợp lệ, cần ít nhất 2.")
            # Thiếu mốc 0 ⇒ bài TRỐNG snap về mốc thấp nhất còn lại: ứng viên không nói gì vẫn được
            # điểm, và KHÔNG lỗi nào nổ. Thiếu mốc maxScore ⇒ luật F13 ("sampleAnswer ở mức ĐIỂM
            # TỐI ĐA") trỏ vào một mức không tồn tại.
            if 0 not in by_score:
                raise ValueError(f"Tiêu chí {cid}: thiếu mốc 0 (mốc 'không có bằng chứng nào').")
            if mx not in by_score:
                raise ValueError(f"Tiêu chí {cid}: thiếu mốc {mx} (điểm tối đa của tiêu chí).")
            for s, desc in sorted(by_score.items()):
                if len(desc) < LEVEL_DESCRIPTOR_MIN_CHARS:
                    raise ValueError(
                        f"Tiêu chí {cid}: mô tả mốc {s} rỗng hoặc quá ngắn "
                        f"(<{LEVEL_DESCRIPTOR_MIN_CHARS} ký tự) — mốc phải mô tả hành vi quan sát "
                        "được, không phải nhãn điểm.")

        missing = set(max_by_id) - set(levels_by_id)
        if missing:
            raise ValueError(f"LLM không trả mốc cho tiêu chí: {sorted(missing)}")

        # Giữ ĐÚNG thứ tự đầu vào — HR đọc mốc cạnh hàng tiêu chí của mình, thứ tự nhảy theo model
        # trả về là nhiễu không cần thiết.
        return [{"criterionId": cid, "levels": levels_by_id[cid]} for cid in max_by_id]

    async def analyze_cv(self, cv_text: str, jd_text: str | None,
                           job_category: str | None,
                         criteria: list[dict] | None = None, language: str = "vi") -> dict:
        """
        Phân tích CV (BC6, B2C sync, D17) — feedback + khớp JD (nếu có).

        Trả về dict:
          { "summary": str, "strengths": [str], "weaknesses": [str],
            "suggestions": [str],
            "jdMatch"?: { "score": int, "matchedSkills": [str], "missingSkills": [str] } }

        jdMatch chỉ xuất hiện khi jd_text được cung cấp.

        C14 (B2B sàng CV) — có ``criteria`` (tiêu chí campaign) thì trả THÊM:
          "fullName": str|None (BK28), "skills": [str], "yearsExperience": float, "education": [str],
          "criterionMatches": [{criterionId, matchScore, reasoning}], "overallMatchScore": int

        ``criteria=None`` (đường B2C) ⇒ prompt, response_schema và dict trả về GIỮ NGUYÊN XI.
        ``criteria`` là tham số có mặc định nên mọi call site 3-đối-số cũ chạy nguyên.
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_cv_analysis_prompt(cv_text, jd_text, job_category, criteria, language=language)

        properties: dict = {
            "summary": {"type": "string"},
            "strengths": {"type": "array", "items": {"type": "string"}},
            "weaknesses": {"type": "array", "items": {"type": "string"}},
            "suggestions": {"type": "array", "items": {"type": "string"}},
        }
        required = ["summary", "strengths", "weaknesses", "suggestions"]
        if criteria:
            properties.update({
                # BK28 — KHÔNG đưa vào `required`: CV không có tên rõ ràng là chuyện HỢP LỆ, mà
                # `required` ở đây nghĩa là model buộc phải bịa ra một chuỗi. `nullable` để model
                # có đường trả null tường minh thay vì điền bừa.
                "fullName": {"type": "string", "nullable": True},
                "skills": {"type": "array", "items": {"type": "string"}},
                "yearsExperience": {"type": "number"},
                "education": {"type": "array", "items": {"type": "string"}},
                "criterionMatches": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "criterionId": {"type": "string"},
                            "matchScore": {"type": "number"},
                            "reasoning": {"type": "string"},
                        },
                        "required": ["criterionId", "matchScore"],
                    },
                },
                "overallMatchScore": {"type": "integer"},
            })
            required += ["skills", "criterionMatches", "overallMatchScore"]
        if jd_text:
            properties["jdMatch"] = {
                "type": "object",
                "properties": {
                    "score": {"type": "integer"},
                    "matchedSkills": {"type": "array", "items": {"type": "string"}},
                    "missingSkills": {"type": "array", "items": {"type": "string"}},
                },
                "required": ["score", "matchedSkills", "missingSkills"],
            }
            required.append("jdMatch")

        response = await self._generate(
            "analyze_cv",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.0,  # phân tích/chấm khớp cần nhất quán, không sáng tạo
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": properties,
                    "required": required,
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM phân tích CV trả về JSON không hợp lệ: {text[:200]}")

        summary = str(data.get("summary", "")).strip()
        if not summary:
            raise ValueError("LLM không trả về tóm tắt CV hợp lệ.")

        def _clean_list(items) -> list[str]:
            if not isinstance(items, list):
                return []
            return [str(i).strip() for i in items if str(i).strip()]

        result: dict = {
            "summary": summary,
            "strengths": _clean_list(data.get("strengths")),
            "weaknesses": _clean_list(data.get("weaknesses")),
            "suggestions": _clean_list(data.get("suggestions")),
        }

        if jd_text:
            jd_match_raw = data.get("jdMatch")
            if not isinstance(jd_match_raw, dict):
                raise ValueError("LLM không trả jdMatch dù request có jdText.")

            # Kẹp điểm khớp trong [0, 100] phòng LLM trả ngoài thang.
            score = float(jd_match_raw.get("score", 0) or 0)
            score = max(0.0, min(score, 100.0))

            result["jdMatch"] = {
                "score": int(round(score)),
                "matchedSkills": _clean_list(jd_match_raw.get("matchedSkills")),
                "missingSkills": _clean_list(jd_match_raw.get("missingSkills")),
            }

        # ── C14 — chấm khớp theo tiêu chí campaign + chống ảo giác (AI-3) ────────────────
        # KHÔNG tin điểm model trả về: kẹp về thang của ĐÚNG tiêu chí đó, DROP id không nằm
        # trong `criteria[]` đã gửi xuống (id bịa = tiêu chí model tự nghĩ ra), bỏ id lặp.
        # Làm ở đây chứ không chỉ trông vào .NET: hai lớp là cố ý — .NET có FK Restrict + clamp,
        # nhưng endpoint /analyze-cv còn được gọi TRỰC TIẾP (B2C sync) nên guard phải nằm tại nguồn.
        if criteria:
            allowed: dict[str, float] = {}
            for c in criteria:
                cid = str(c.get("criterionId") or "").strip()
                if cid:
                    allowed[cid] = float(c.get("maxScore") or 0)

            matches: list[dict] = []
            seen: set[str] = set()
            for m in data.get("criterionMatches") or []:
                if not isinstance(m, dict):
                    continue
                cid = str(m.get("criterionId") or "").strip()
                if cid not in allowed or cid in seen:
                    continue    # id BỊA hoặc trùng → bỏ
                seen.add(cid)
                raw_score = m.get("matchScore")
                try:
                    match_score = float(raw_score if raw_score is not None else 0)
                except (TypeError, ValueError):
                    match_score = 0.0
                reasoning = str(m.get("reasoning") or "").strip()
                matches.append({
                    "criterionId": cid,
                    # Kẹp [0, maxScore] của CHÍNH tiêu chí này (không phải một thang chung).
                    "matchScore": max(0.0, min(match_score, allowed[cid])),
                    "reasoning": reasoning or None,
                })

            if not matches:
                # 0 tiêu chí nào sống sót = model bịa sạch id hoặc bỏ trắng phần chấm. Nếu cứ trả
                # về thì Campaign lưu candidate "Analyzed" mà KHÔNG có điểm tiêu chí nào — HR nhìn
                # thấy đã chấm xong trong khi thực chất chưa chấm gì. Raise ⇒ worker retry, hết
                # retry thì cv-failed (HR biết mà cho chạy lại), thà thấy lỗi còn hơn sai lặng lẽ.
                raise ValueError("LLM không trả criterionMatches hợp lệ nào (id bịa hoặc rỗng).")
            if len(matches) < len(allowed):
                print(f"[⚠️] Sàng CV: chỉ {len(matches)}/{len(allowed)} tiêu chí được chấm hợp lệ")

            overall = data.get("overallMatchScore")
            try:
                overall = float(overall if overall is not None else 0)
            except (TypeError, ValueError):
                overall = 0.0
            years = data.get("yearsExperience")
            try:
                years = float(years) if years is not None else None
            except (TypeError, ValueError):
                years = None

            # BK28 — `fullName` là DANH TÍNH đi thẳng vào bảng shortlist + bản xuất CSV/PDF của HR,
            # nên guard đặt tại NGUỒN (AI-3) chứ không chỉ trông vào .NET — endpoint /analyze-cv còn
            # được gọi TRỰC TIẾP, y như lý do 2 lớp của `criterionMatches` ở trên.
            #   • rỗng/toàn khoảng trắng ⇒ None (CV không có tên rõ ràng là HỢP LỆ, đừng lưu "");
            #   • cắt 255 = đúng `varchar(255)` của `cv_submission.full_name`, tràn thì Postgres ném
            #     lúc SaveChanges ⇒ callback 500 ⇒ worker nack ⇒ vòng lặp republish.
            # 🔴 CỐ Ý KHÔNG raise khi thiếu/rỗng (khác `criterionMatches`): `cv_screening.py` biến
            # ValueError thành retry `score_max_attempts` lần rồi `PermanentCvError` ⇒ ứng viên rơi
            # `AnalysisFailed` và KHÔNG có endpoint nào cho HR chạy lại. Biến một field phụ thành
            # đường làm hỏng cả hồ sơ đắt hơn nhiều so với việc thiếu một cái tên.
            full_name = str(data.get("fullName") or "").strip()
            result["fullName"] = full_name[:255] or None
            result["skills"] = _clean_list(data.get("skills"))
            result["yearsExperience"] = max(0.0, years) if years is not None else None
            result["education"] = _clean_list(data.get("education"))
            result["criterionMatches"] = matches
            result["overallMatchScore"] = int(round(max(0.0, min(overall, 100.0))))

        return result

    async def analyze_repo(self, repo_digest: str, jd_text: str | None,
                           job_category: str | None, language: str = "vi") -> dict:
        # F21: mọi hàm build prompt phải nạp registry trước, kể cả prompt in-code.
        await prompt_registry.refresh_if_stale()
        prompt = build_repo_analysis_prompt(repo_digest, jd_text, job_category, language=language)
        properties: dict = {
            "summary": {"type": "string"},
            "techStack": {"type": "array", "items": {"type": "string"}},
            "strengths": {"type": "array", "items": {"type": "string"}},
            "weaknesses": {"type": "array", "items": {"type": "string"}},
            "suggestions": {"type": "array", "items": {"type": "string"}},
            "interviewTalkingPoints": {"type": "array", "items": {"type": "string"}},
        }
        required = list(properties)
        if jd_text:
            properties["jdMatch"] = {"type": "object", "properties": {
                "score": {"type": "integer"},
                "matchedSkills": {"type": "array", "items": {"type": "string"}},
                "missingSkills": {"type": "array", "items": {"type": "string"}},
            }, "required": ["score", "matchedSkills", "missingSkills"]}
            required.append("jdMatch")
        response = await self._generate("analyze_repo", contents=prompt,
            config=types.GenerateContentConfig(temperature=0.0, response_mime_type="application/json",
                response_schema={"type": "object", "properties": properties, "required": required}))
        try:
            data = json.loads((response.text or "").strip())
        except json.JSONDecodeError:
            raise ValueError("LLM phân tích repository trả JSON không hợp lệ.")
        summary = str(data.get("summary", "")).strip()
        if not summary:
            raise ValueError("LLM không trả về tóm tắt repository hợp lệ.")
        def clean(value) -> list[str]:
            return [str(x).strip() for x in value if str(x).strip()] if isinstance(value, list) else []
        result = {"summary": summary, "techStack": clean(data.get("techStack")),
                  "strengths": clean(data.get("strengths")), "weaknesses": clean(data.get("weaknesses")),
                  "suggestions": clean(data.get("suggestions")),
                  "interviewTalkingPoints": clean(data.get("interviewTalkingPoints"))}
        if jd_text:
            match = data.get("jdMatch")
            if not isinstance(match, dict):
                raise ValueError("LLM không trả jdMatch dù request có jdText.")
            result["jdMatch"] = {"score": int(round(max(0, min(float(match.get("score", 0) or 0), 100)))),
                "matchedSkills": clean(match.get("matchedSkills")), "missingSkills": clean(match.get("missingSkills"))}
        return result

    async def score(self, question: str, transcript: str,
                    job_category: str, criteria: list[dict],
                    temperature: float = 0.0,
                    delivery: dict | None = None, language: str = "vi",
                    sample_answer: str | None = None) -> list[dict]:
        """
        Chấm 1 câu trả lời theo rubric.

        delivery (F11 — FR06, optional): chỉ số cách nói đo từ audio (tốc độ nói, khoảng lặng,
          từ đệm) — ghép vào prompt để tiêu chí "độ trôi chảy" chấm bằng SỐ ĐO thay vì đoán
          từ text. ``None`` (mặc định) = chưa đo được → prompt nói rõ + cấm bịa số.

        criteria: list dict từ C# gửi qua, mỗi phần tử có
          { criterionId, name, description, maxScore, weight,
            levels: [{score, descriptor}], anchors?: [{score, exampleAnswer}] }

        temperature (E10 — self-consistency): nhiệt độ sinh. Attempt 1 = 0.0 (tái lập); attempt 2..N
          = SelfConsistencyTemperature (>0) để tạo dao động THẬT giữa các lần chấm → .NET đo spread
          (max−min) → gắn cờ needs_review khi phân tán. Mặc định 0.0 (giữ hành vi cũ / worker cũ).

        Trả về: ``ScoreOutcome(scores, sample_answer)`` — F13 đổi shape (trước là list trần).
          scores: list dict (E9 — neo theo mức)
            [ { "criterionId": str, "score": float, "levelMatched": int, "reasoning": str }, ... ]
            với score == levelMatched (score luôn = điểm của 1 mức HỢP LỆ của tiêu chí).
          sample_answer: câu trả lời mẫu mức tối đa cho ĐÚNG câu hỏi này (F13), hoặc None.
        """
        # Map criterionId -> maxScore + tập điểm mức HỢP LỆ (chấp cả key hoa/thường).
        # Nguồn mức: levels C# gửi (rubric_levels khai hoặc dải mặc định 0..maxScore).
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        # BK23 — chụp con dấu NGAY sau lượt refresh vừa rồi và TRƯỚC khi dựng prompt bên dưới:
        # đây chính là bộ mảnh sắp đi vào `build_scoring_prompt`. Đọc muộn hơn (ở worker, sau khi
        # score() trả về) có thể vớ phải một lượt refresh khác — con dấu sẽ nói dối về thước đo.
        stamp = prompt_registry.prompt_version()
        max_by_id: dict[str, int] = {}
        levels_by_id: dict[str, list[int]] = {}
        for c in criteria:
            cid = str(c.get("criterionId") or c.get("CriterionId"))
            mx = int(c.get("maxScore") or c.get("MaxScore") or 5)
            max_by_id[cid] = mx

            raw_levels = c.get("levels") or c.get("Levels") or []
            scores: set[int] = set()
            for lv in raw_levels:
                s = lv.get("score") if isinstance(lv, dict) else lv
                if s is not None:
                    scores.add(int(s))
            # Không có levels (phòng hờ) → dải mặc định 0..maxScore.
            levels_by_id[cid] = sorted(scores) if scores else list(range(0, mx + 1))

        prompt = build_scoring_prompt(question, transcript, job_category, criteria, delivery,
                                      language=language, sample_answer=sample_answer)

        response = await self._generate(
            "score",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=temperature,  # E9 mặc định 0 (nhất quán); E10 attempt 2..N > 0 để đo spread
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "scores": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "criterionId": {"type": "string"},
                                    "score": {"type": "number"},
                                    "levelMatched": {"type": "integer"},
                                    "reasoning": {"type": "string"},
                                },
                                "required": ["criterionId", "score", "levelMatched", "reasoning"],
                            },
                        },
                        # F13 — câu trả lời mẫu mức tối đa cho ĐÚNG câu hỏi này.
                        # Sinh CÙNG lượt chấm: prompt đã mang câu hỏi + rubric + transcript
                        # nên chi phí tăng thêm CHỈ là output token; gọi riêng lúc user mở
                        # sẽ phải nạp lại toàn bộ ngần ấy input.
                        "sampleAnswer": {"type": "string"},
                    },
                    "required": ["scores", "sampleAnswer"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM chấm trả về JSON không hợp lệ: {text[:200]}")

        raw = data.get("scores", [])
        if not isinstance(raw, list) or not raw:
            raise ValueError("LLM không trả về điểm hợp lệ.")

        results: list[dict] = []
        seen: set[str] = set()
        for item in raw:
            cid = str(item.get("criterionId", "")).strip()
            if not cid or cid not in max_by_id:
                # Gemini bịa criterionId không có trong rubric -> bỏ.
                continue
            if cid in seen:
                continue  # tránh trùng tiêu chí
            seen.add(cid)

            valid_levels = levels_by_id[cid]

            # E9 — NEO theo mức: điểm phải là 1 mức HỢP LỆ của tiêu chí.
            # Kẹp trước để snap ổn định (phòng LLM trả ngoài thang).
            raw_score = max(0.0, min(float(item.get("score", 0)), float(max_by_id[cid])))
            lm = item.get("levelMatched")

            if isinstance(lm, (int, float)) and int(lm) in valid_levels:
                level = int(lm)
            else:
                # LLM không trả mức hợp lệ -> SNAP về mức gần điểm nhất (khớp defense C#:
                # không raise/không drop -> tránh thiếu tiêu chí => answer Failed, INT-9).
                level = min(valid_levels, key=lambda v: (abs(v - raw_score), v))

            # E11 — chuẩn "NHẬN XÉT OK": reasoning RỖNG = output malformed (AI không tuân
            # schema bắt buộc dẫn chứng) -> reject như idiom score() (missing criterion/JSON lỗi).
            # temperature=0 nên lỗi tái lập -> worker coi là PermanentError. Chỉ chặn RỖNG ở đây
            # (an toàn, không phá điểm hợp lệ); "quá ngắn" là cờ MỀM do .NET flag needs_review
            # (không mất điểm — HR chốt). Xem interview.md §E11 + ScoringOptions.MinReasoningLen.
            reasoning = str(item.get("reasoning", "")).strip()
            if not reasoning:
                raise ValueError(
                    f"Tiêu chí {cid}: reasoning rỗng — nhận xét bắt buộc có dẫn chứng từ "
                    "câu trả lời (E11)."
                )

            results.append({
                "criterionId": cid,
                "score": float(level),   # E9: score = levelMatched.score
                "levelMatched": level,
                "reasoning": reasoning,
            })

        # Đảm bảo chấm đủ mọi tiêu chí; thiếu cái nào -> coi như lỗi để retry.
        missing = set(max_by_id.keys()) - seen
        if missing:
            raise ValueError(f"LLM chấm thiếu tiêu chí: {missing}")

        # F13 — câu trả lời mẫu. KHÔNG raise khi thiếu/rỗng: đây là phần PHỤ TRỢ, để nó
        # đánh hỏng cả lượt chấm (=> answer Failed, mất credit PAY-13) là đổi chác tồi.
        # Thiếu -> None -> .NET không lưu -> FE đơn giản không hiện mục gợi ý.
        sample = data.get("sampleAnswer")
        sample = sample.strip() if isinstance(sample, str) else None

        return ScoreOutcome(scores=results, sample_answer=sample or None, prompt_version=stamp)

    async def generate_roadmap(self, job_category: str, level: str,
                               weaknesses: list[dict] | None,
                               cv_text: str | None,
                               focus: str | None = None,
                               cv_analysis_summary: str | None = None,
                               prior_roadmap_summary: str | None = None,
                               grounding: list[dict] | None = None, language: str = "vi") -> list[dict]:
        """
        BC13/D20 — sinh cấu trúc roadmap ôn tập (sync, stateless, KHÔNG ghi DB).

        BC17 — focus/cvAnalysisSummary/priorRoadmapSummary: cá nhân hoá theo report ứng viên CHỌN
        + ô mô tả mong muốn. Đều là DỮ LIỆU (bọc delimiter trong prompt, AI-4).

        ``grounding`` (RAG, Contract 2): tài liệu uy tín — chèn làm căn cứ định hình cấu trúc.
        Cấu trúc roadmap KHÔNG emit citation ở Phase 1 → output shape KHÔNG đổi (list dict cũ).

        Trả về: list dict milestone
          [ { "title": str, "focusCriteria": [str], "lessons": [{"title": str}] }, ... ]
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_roadmap_prompt(
            job_category, level, weaknesses, cv_text,
            focus=focus,
            cv_analysis_summary=cv_analysis_summary,
            prior_roadmap_summary=prior_roadmap_summary,
            grounding=grounding, language=language,
        )

        response = await self._generate(
            "generate_roadmap",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.4,  # cấu trúc kế hoạch — nhất quán hơn sinh câu hỏi tự do
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "milestones": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "title": {"type": "string"},
                                    "focusCriteria": {
                                        "type": "array",
                                        "items": {"type": "string"},
                                    },
                                    "lessons": {
                                        "type": "array",
                                        "items": {
                                            "type": "object",
                                            "properties": {
                                                "title": {"type": "string"},
                                            },
                                            "required": ["title"],
                                        },
                                    },
                                },
                                "required": ["title", "focusCriteria", "lessons"],
                            },
                        }
                    },
                    "required": ["milestones"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM sinh roadmap trả về JSON không hợp lệ: {text[:200]}")

        raw = data.get("milestones", [])
        if not isinstance(raw, list) or not raw:
            raise ValueError("LLM không trả về milestone hợp lệ.")

        milestones: list[dict] = []
        for m in raw:
            if not isinstance(m, dict):
                continue
            title = str(m.get("title", "")).strip()
            if not title:
                continue  # bỏ milestone bịa không có title

            focus = [str(f).strip() for f in (m.get("focusCriteria") or []) if str(f).strip()]

            lessons: list[dict] = []
            for l in (m.get("lessons") or []):
                if not isinstance(l, dict):
                    continue
                l_title = str(l.get("title", "")).strip()
                if l_title:
                    lessons.append({"title": l_title})
            if not lessons:
                continue  # milestone không có lesson nào hợp lệ -> bỏ

            milestones.append({"title": title, "focusCriteria": focus, "lessons": lessons})

        if not milestones:
            raise ValueError("LLM trả về roadmap rỗng sau khi lọc.")

        return milestones

    async def generate_lesson_theory(self, job_category: str, level: str,
                                     lesson_title: str, focus_criteria: list[str],
                                     weaknesses: list[str] | None,
                                     grounding: list[dict] | None = None, language: str = "vi"
                                     ) -> LessonTheoryResult:
        """BC13/D20 — sinh lý thuyết (Markdown, tiếng Việt) + F15 tài liệu học.

        Trả ``LessonTheoryResult(theory, resources, cited_chunk_ids)`` — RAG grounding thêm
        ``cited_chunk_ids`` (đổi shape so với trước, mẫu F13). ``resources`` đã qua
        :func:`app.resources.sanitize_resources`: url KHÔNG thuộc allowlist tên
        miền bị BỎ CẢ MỤC. Xem docstring app/resources.py cho lý
        do — tóm tắt: LLM sinh url là đoán chuỗi, domain bịa là rủi ro thật.

        resources rỗng KHÔNG phải lỗi (lý thuyết vẫn dùng được) → không raise,
        khác với theoryMarkdown rỗng.

        ``grounding`` (RAG, Contract 2): tài liệu uy tín — chèn làm căn cứ + đòi trích dẫn.
        ``cited_chunk_ids`` = None khi ungrounded (endpoint không trả field, giữ shape cũ);
        ⊆ tập grounding đã cấp khi grounded (drop id lạ = chống bịa).
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()

        grounded = bool(grounding)
        response_properties: dict = {
            "sections": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "criterion": {"type": "string"},
                        "heading": {"type": "string"},
                        "body": {"type": "string"},
                    },
                    "required": ["criterion", "body"],
                },
            },
            "example": {"type": "string"},
            "commonMistakes": {"type": "string"},
            "resources": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "title": {"type": "string"},
                        "type": {"type": "string"},
                        "publisher": {"type": "string"},
                        "url": {"type": "string"},
                    },
                    "required": ["title", "type"],
                },
            },
        }
        if grounded:
            response_properties["citedChunkIds"] = {
                "type": "array", "items": {"type": "string"}}

        config = types.GenerateContentConfig(
            temperature=0.5,  # nội dung giảng dạy — có ví dụ, không quá tất định
            response_mime_type="application/json",
            response_schema={
                "type": "object",
                "properties": response_properties,
                "required": ["sections", "example", "commonMistakes"],
            },
        )

        # Bài trượt rubric thì TRẢ LẠI kèm nhận xét và bắt viết lại, thay vì lưu một bài không dùng
        # được (lý thuyết chỉ sinh một lần rồi lưu ⇒ bài hỏng sống vĩnh viễn). Hỏi lại y hệt đề cũ
        # thì phần lớn nhận lại đúng cái sai đó, nên lượt sau mang theo `retry_feedback`.
        attempts = max(1, settings.lesson_theory_max_attempts)
        feedback: str | None = None
        last_defects: list[str] = []

        for _ in range(attempts):
            prompt = build_lesson_theory_prompt(
                job_category, level, lesson_title, focus_criteria, weaknesses,
                grounding, retry_feedback=feedback, language=language)

            # F22 — lượt gọi DUY NHẤT hoãn ghi nhận (defer_report): số liệu đáng giá ở
            # đây không chỉ là token mà còn là "AI bịa tên miền bao nhiêu lần" (allowlist
            # F15 hiện loại URL trong IM LẶNG — nếu Gemini bịa domain 90% số lần thì
            # không ai biết). Con số đó chỉ có SAU khi parse, nên phải hoãn.
            # try/finally BẮT BUỘC, và phải nằm TRONG vòng lặp: token của lượt bị trả lại vẫn đã
            # bị đốt: đó đúng là phần chi phí cần thấy nhất, gom ra ngoài là mất hẳn.
            response = await self._generate(
                "generate_lesson_theory",
                defer_report=True,
                contents=prompt,
                config=config,
            )

            url_meta: dict | None = None
            try:
                text = (response.text or "").strip()
                try:
                    data = json.loads(text)
                except json.JSONDecodeError:
                    data = None

                if not isinstance(data, dict):
                    last_defects = [lesson_message("not_json", language,
                                                   raw=text[:200])]
                    feedback = "\n".join(f"- {d}" for d in last_defects)
                    continue

                url_meta = count_rejected_urls(data.get("resources"))

                last_defects = evaluate_lesson_theory(
                    data, focus_criteria, lesson_title, language=language)
                if last_defects:
                    # Log để tỉ lệ trả-lại ĐO ĐƯỢC. Không có dòng này thì rubric siết quá tay sẽ chỉ
                    # lộ ra dưới dạng "thỉnh thoảng mở bài bị 502" — đúng kiểu hỏng im lặng mà
                    # allowlist URL F15 đã dính (loại link mà không ai biết tỉ lệ).
                    logger.info('Bài giảng "%s" bị trả lại: %s',
                                lesson_title, "; ".join(last_defects))
                    feedback = "\n".join(f"- {d}" for d in last_defects)
                    continue

                theory = render_lesson_markdown(lesson_title, data,
                                                language=language)
                resources = sanitize_resources(data.get("resources"))

                cited: list[str] | None = None
                if grounded:
                    allowed = {str(g.get("chunkId")).strip()
                               for g in grounding if g.get("chunkId")}
                    cited = [c.strip() for c in (data.get("citedChunkIds") or [])
                             if isinstance(c, str) and c.strip() in allowed]
                    cited = list(dict.fromkeys(cited))  # bỏ trùng, giữ thứ tự

                return LessonTheoryResult(theory=theory, resources=resources,
                                          cited_chunk_ids=cited)
            finally:
                await report_usage("generate_lesson_theory", settings.gemini_model,
                                   response, meta=url_meta)

        # Hết lượt vẫn chưa đạt → ValueError ⇒ InterviewService nhận 502 và KHÔNG lưu gì, nên lần
        # người học mở lại sẽ sinh lại. Thà không có bài còn hơn có một bài rỗng đóng đinh vĩnh viễn.
        raise ValueError(
            "LLM không sinh được bài giảng đạt yêu cầu sau "
            f"{attempts} lượt: " + "; ".join(last_defects))

    async def summarize_roadmap(self, job_category: str, level: str,
                                criteria_progress: list[dict], language: str = "vi") -> dict:
        """
        BC13/D20 — tổng kết roadmap: mạnh/yếu/cải thiện + nhận xét chung.

        Trả về dict:
          { "strengths": [str], "weaknesses": [str], "improvements": [str],
            "overallComment": str }
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_summarize_roadmap_prompt(job_category, level, criteria_progress, language=language)

        response = await self._generate(
            "summarize_roadmap",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.0,  # tổng kết dựa số liệu khách quan — cần nhất quán
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "strengths": {"type": "array", "items": {"type": "string"}},
                        "weaknesses": {"type": "array", "items": {"type": "string"}},
                        "improvements": {"type": "array", "items": {"type": "string"}},
                        "overallComment": {"type": "string"},
                    },
                    "required": ["strengths", "weaknesses", "improvements", "overallComment"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM tổng kết roadmap trả về JSON không hợp lệ: {text[:200]}")

        comment = str(data.get("overallComment", "")).strip()
        if not comment:
            raise ValueError("LLM không trả về nhận xét tổng kết hợp lệ.")

        def _clean_list(items) -> list[str]:
            if not isinstance(items, list):
                return []
            return [str(i).strip() for i in items if str(i).strip()]

        return {
            "strengths": _clean_list(data.get("strengths")),
            "weaknesses": _clean_list(data.get("weaknesses")),
            "improvements": _clean_list(data.get("improvements")),
            "overallComment": comment,
        }

    async def summarize_session(self, job_category: str, overall_score: float,
                                criteria_scores: list[dict], language: str = "vi") -> dict:
        """
        BC10 — nhận xét chung 1 buổi luyện B2C (sync, best-effort, KHÔNG ghi DB).

        criteria_scores: list dict từ Interview gửi qua, mỗi phần tử
          { "name": str, "percentage": float, "needsImprovement": bool }

        Trả về dict: { "overallComment": str }
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_summarize_session_prompt(job_category, overall_score, criteria_scores, language=language)

        response = await self._generate(
            "summarize_session",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.4,  # nhận xét tự nhiên — đủ thấp để bám số liệu, mềm hơn tổng kết
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "overallComment": {"type": "string"},
                    },
                    "required": ["overallComment"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM tổng kết buổi luyện trả về JSON không hợp lệ: {text[:200]}")

        comment = str(data.get("overallComment", "")).strip()
        if not comment:
            raise ValueError("LLM không trả về nhận xét buổi luyện hợp lệ.")

        return {"overallComment": comment}

    async def decide_next(self, job_category: str, current_question: str, transcript: str,
                          history: list[dict], asked_count: int, follow_up_count: int,
                          max_questions: int, max_follow_ups: int,
                          criteria: list[dict],
                          root_question: str | None = None, current_depth: int = 0,
                          max_depth: int = 0,
                          other_topics: list[str] | None = None, language: str = "vi",
                          seniority: str = "Junior", current_evidence_state: list[dict] | None = None) -> dict:
        """Phỏng vấn THÍCH ỨNG — quyết định hành động kế tiếp (sync, stateless, KHÔNG ghi DB).

        Trả về dict: { "action": str, "nextQuestion": str|None, "reason": str|None }
          action ∈ {follow_up, clarify, new_question, end}; nextQuestion None ⇔ end.

        temperature=0.3: bám sát câu trả lời/năng lực nhưng câu hỏi tự nhiên hơn chấm điểm
        (0.0) — thấp hơn sinh câu hỏi tự do (0.7) vì phải nhắm đúng câu trả lời + tiêu chí.

        INT-17b — ``max_depth > 0`` = chế độ CHUỖI (đào sâu theo từng câu gốc). Tập action HỢP LỆ
        giữ nguyên 4 giá trị để không phá hợp đồng với InterviewService; prompt chỉ thôi CHÀO
        ``new_question``, còn phía .NET coi nó là "hết chuỗi" (không append).
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()

        # Đường này chạy ĐỒNG BỘ trong request upload của người dùng ⇒ độ trễ là chi phí trực
        # tiếp lên trải nghiệm. Suy luận ẩn của Gemini 2.5 đo được chiếm ~3/4 thời gian mà không
        # đổi quyết định (chi tiết + số liệu A/B: `config.decide_next_thinking_budget`).
        # `-1` = không đụng vào, để model tự quyết như trước.
        cfg = dict(
            temperature=0.3,
            response_mime_type="application/json",
            response_schema={
                "type": "object",
                "properties": {
                    "action": {"type": "string"},
                    "nextQuestion": {"type": "string"},
                    "reason": {"type": "string"},
                    "targetCriterionId": {"type": "string", "nullable": True},
                    "evidenceFound": {"type": "array", "items": {"type": "string"}},
                    "missingEvidence": {"type": "array", "items": {"type": "string"}},
                    "newEvidenceState": {"type": "string", "nullable": True},
                },
                "required": ["action"],
            },
        )
        if settings.decide_next_thinking_budget >= 0:
            cfg["thinking_config"] = types.ThinkingConfig(
                thinking_budget=settings.decide_next_thinking_budget)

        # Q16 — output hỏng thì TRẢ LẠI và hỏi lại kèm lý do, thay vì raise thẳng thành 502 ngay
        # lượt đầu (mẫu `generate_lesson_theory`). Hỏi lại y hệt đề cũ thì phần lớn nhận lại đúng
        # cái sai đó, nên lượt sau mang theo `retry_feedback`.
        attempts = max(1, settings.decide_next_max_attempts)
        feedback: str | None = None
        last_error: ValueError | None = None

        for _ in range(attempts):
            prompt = build_decide_next_prompt(
                job_category, current_question, transcript, history,
                asked_count, follow_up_count, max_questions, max_follow_ups, criteria,
                root_question=root_question, current_depth=current_depth,
                max_depth=max_depth, other_topics=other_topics, language=language,
                seniority=seniority, current_evidence_state=current_evidence_state,
                retry_feedback=feedback)

            response = await self._generate(
                "decide_next",
                contents=prompt,
                config=types.GenerateContentConfig(**cfg),
            )

            try:
                return self._parse_decide_next(response)
            except ValueError as e:
                last_error = e
                feedback = str(e)
                # Log để tỉ lệ trả-lại ĐO ĐƯỢC + chốt nguyên nhân Q16 bằng số thật (xem
                # `_generation_diagnostics`). Không có dòng này thì câu cụt chỉ lộ ra dưới dạng
                # "thỉnh thoảng hội thoại thích ứng chết" — đúng kiểu hỏng im lặng mà allowlist URL
                # F15 đã dính.
                logger.info("Câu kế bị trả lại (%s): %s", _generation_diagnostics(response), e)

        raise last_error  # type: ignore[misc]  # attempts >= 1 ⇒ luôn đã gán

    @staticmethod
    def _parse_decide_next(response) -> dict:
        """Đọc + KIỂM output một lượt `/decide-next`. Raise ``ValueError`` khi không dùng được.

        Tách khỏi vòng lặp để chỗ "thế nào là output hợp lệ" nằm gọn một nơi và test được trực
        tiếp, không phải đi vòng qua mock SDK.
        """
        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM quyết định câu kế trả về JSON không hợp lệ: {text[:200]}")

        action = str(data.get("action", "")).strip().lower()
        if action not in {"follow_up", "clarify", "new_question", "end"}:
            raise ValueError(f"LLM trả về action không hợp lệ: {action!r}")

        next_q = str(data.get("nextQuestion", "") or "").strip()
        # ≠ end BẮT BUỘC có câu hỏi — rỗng = output malformed → reject (idiom score()).
        if action != "end" and not next_q:
            raise ValueError(f"Action {action} nhưng nextQuestion rỗng.")

        # Q16 — rỗng KHÔNG phải hình dạng hỏng duy nhất. Trên deploy 2026-08-07 một câu `Clarify`
        # 31 ký tự ("Bạn có thể giải thích rõ hơn về") đã tới tay ứng viên, được trả lời, rồi được
        # chấm. Câu cụt tệ hơn không có câu: không có câu thì .NET degrade về luồng tĩnh và ứng
        # viên không mất gì, còn nửa câu thì họ trả lời một thứ vô nghĩa bằng chính lượt đã trả tiền.
        if action != "end" and _looks_truncated(next_q):
            raise ValueError(
                "nextQuestion là câu chưa hoàn chỉnh (bị cắt giữa chừng hoặc chỉ có dấu câu): "
                f"{next_q!r}. Viết lại MỘT câu hỏi trọn vẹn, kết thúc bằng dấu câu.")

        reason = str(data.get("reason", "") or "").strip() or None
        target_criterion_id = str(data.get("targetCriterionId", "") or "").strip() or None

        # ── Nhãn bằng chứng: FAIL-OPEN CÓ CHỦ ĐÍCH ──────────────────────────────────────────
        # Ba trường dưới đây là nhãn PHỤ TRỢ cho state phía .NET, không phải kết quả của lượt gọi.
        # Trước đây chúng `raise ValueError` khi model trả sai hình dạng → hết `decide_next_max_attempts`
        # → `main.py` gói thành **502** → buổi phỏng vấn chết vì một cái nhãn. Đó là chính sách NGƯỢC với
        # `targetCriterionIds` ngay ở `generate()` cho CÙNG hạng nhãn ("biến một cái nhãn phụ thành đường
        # làm hỏng cả buổi thì đắt hơn nhiều"), và `schemas.DecideNextResponse` khai cả ba `| None = None`
        # nên .NET vốn chịu được chúng vắng mặt.
        #
        # Nhãn hỏng ⇒ bỏ qua + LOG (đo được), giữ nguyên `action`/`nextQuestion` — thứ ứng viên thật sự
        # cần. Vẫn KHÔNG nới cho `action`/`nextQuestion`: những cái đó hỏng thì lượt gọi vô dụng, trả lại
        # là đúng (Q16).
        def evidence_list(field: str) -> list[str]:
            value = data.get(field)
            if value is None:
                return []
            if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
                logger.info("Bỏ qua %s không phải mảng chuỗi: %r", field, value)
                return []
            return [item.strip() for item in value if item.strip()]

        evidence_found = evidence_list("evidenceFound")
        missing_evidence = evidence_list("missingEvidence")
        new_evidence_state = str(data.get("newEvidenceState", "") or "").strip().upper() or None
        if new_evidence_state not in {None, "UNKNOWN", "PARTIAL", "SATISFIED", "FAILED"}:
            logger.info("Bỏ qua newEvidenceState không hợp lệ: %r", new_evidence_state)
            new_evidence_state = None
        return {
            "action": action,
            "nextQuestion": next_q or None,   # end → None
            "reason": reason,
            "targetCriterionId": target_criterion_id,
            "evidenceFound": evidence_found,
            "missingEvidence": missing_evidence,
            "newEvidenceState": new_evidence_state,
        }
    # ── TTS: đọc câu hỏi thành tiếng ────────────────────────────────────────────
    async def synthesize_speech(self, text: str, voice: str,
                                language_code: str) -> tuple[bytes, str | None]:
        """Text → (PCM thô, mime_type). KHÔNG phải mp3 — caller encode (app/audio.py).

        Trả về PCM 16-bit little-endian mono 24kHz; mime_type dạng
        `audio/L16;codec=pcm;rate=24000` để caller đọc đúng sample-rate.

        AI-4: `text` là NỘI DUNG CÂU HỎI do AI sinh ⇒ coi là DỮ LIỆU, không phải lệnh.
        Ở đây không ghép prompt/chỉ thị nào quanh nó — đưa nguyên văn cho bộ đọc. Model
        TTS chỉ đọc, không "làm theo", nên bề mặt injection gần như bằng 0; thêm chỉ thị
        kiểu "hãy đọc câu sau" mới là chỗ để câu hỏi độc hại bám vào mà lái giọng đọc."""
        # F22: TTS dùng MODEL RIÊNG (settings.tts_model) và tính giá riêng — đi qua
        # cùng chokepoint nhưng khai model tường minh, đừng để nó bị ghi nhầm vào
        # đơn giá của model chat.
        response = await self._generate(
            "text_to_speech",
            model=settings.tts_model,
            contents=text,
            config=types.GenerateContentConfig(
                response_modalities=["AUDIO"],
                speech_config=types.SpeechConfig(
                    language_code=language_code,
                    voice_config=types.VoiceConfig(
                        prebuilt_voice_config=types.PrebuiltVoiceConfig(voice_name=voice),
                    ),
                ),
            ),
        )

        # Audio nằm ở inline_data của part đầu (khác luồng text: response.text = None).
        blob = None
        for candidate in (response.candidates or []):
            content = getattr(candidate, "content", None)
            for part in (getattr(content, "parts", None) or []):
                if getattr(part, "inline_data", None) is not None:
                    blob = part.inline_data
                    break
            if blob is not None:
                break

        data = getattr(blob, "data", None) if blob is not None else None
        if not data:
            raise ValueError("Gemini TTS không trả về audio.")

        return data, getattr(blob, "mime_type", None)
