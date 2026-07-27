import json
from typing import NamedTuple

from google import genai
from google.genai import types

from app.config import settings
from app.resources import sanitize_resources, count_rejected_urls
from app import prompt_registry
from app.prompts import (
    build_prompt, build_scoring_prompt, build_criteria_prompt,
    build_cv_analysis_prompt,
    build_roadmap_prompt, build_lesson_theory_prompt, build_summarize_roadmap_prompt,
    build_summarize_session_prompt, build_decide_next_prompt,
)
from app.providers.base import QuestionProvider
from app.usage import report_usage


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


class GeminiProvider(QuestionProvider):
    def __init__(self) -> None:
        self._client = genai.Client(api_key=settings.gemini_api_key)

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
                       focus_criteria: list[str] | None = None) -> list[str]:
        # F2b — số câu do caller quyết định; settings.question_count chỉ còn là MẶC ĐỊNH khi không gửi.
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        effective_count = count if count is not None else settings.question_count
        prompt = build_prompt(job_category, cv_text, jd_text, effective_count, focus_criteria)

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
                            "items": {"type": "string"},
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

        questions = data.get("questions", [])
        if not isinstance(questions, list) or not questions:
            raise ValueError("LLM không trả về danh sách câu hỏi hợp lệ.")

        questions = [str(q).strip() for q in questions if str(q).strip()]
        if not questions:
            raise ValueError("LLM trả về danh sách câu hỏi rỗng sau khi lọc.")

        return questions[:effective_count]

    async def suggest_criteria(self, job_category: str, jd_text: str | None,
                               criteria_text: str | None, count: int) -> list[dict]:
        """Đề xuất bộ tiêu chí CÓ CẤU TRÚC (C8) — weight chuẩn hoá tổng = 1."""
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_criteria_prompt(job_category, jd_text, criteria_text, count)
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

    async def analyze_cv(self, cv_text: str, jd_text: str | None,
                         job_category: str | None) -> dict:
        """
        Phân tích CV (BC6, B2C sync, D17) — feedback + khớp JD (nếu có).

        Trả về dict:
          { "summary": str, "strengths": [str], "weaknesses": [str],
            "suggestions": [str],
            "jdMatch"?: { "score": int, "matchedSkills": [str], "missingSkills": [str] } }

        jdMatch chỉ xuất hiện khi jd_text được cung cấp.
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_cv_analysis_prompt(cv_text, jd_text, job_category)

        properties: dict = {
            "summary": {"type": "string"},
            "strengths": {"type": "array", "items": {"type": "string"}},
            "weaknesses": {"type": "array", "items": {"type": "string"}},
            "suggestions": {"type": "array", "items": {"type": "string"}},
        }
        required = ["summary", "strengths", "weaknesses", "suggestions"]
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

        return result

    async def score(self, question: str, transcript: str,
                    job_category: str, criteria: list[dict],
                    temperature: float = 0.0,
                    delivery: dict | None = None) -> list[dict]:
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

        prompt = build_scoring_prompt(question, transcript, job_category, criteria, delivery)

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
                               prior_roadmap_summary: str | None = None) -> list[dict]:
        """
        BC13/D20 — sinh cấu trúc roadmap ôn tập (sync, stateless, KHÔNG ghi DB).

        BC17 — focus/cvAnalysisSummary/priorRoadmapSummary: cá nhân hoá theo report ứng viên CHỌN
        + ô mô tả mong muốn. Đều là DỮ LIỆU (bọc delimiter trong prompt, AI-4).

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
                                     weaknesses: list[str] | None) -> tuple[str, list[dict]]:
        """BC13/D20 — sinh lý thuyết (Markdown, tiếng Việt) + F15 tài liệu học.

        Trả ``(theoryMarkdown, resources)``. ``resources`` đã qua
        :func:`app.resources.sanitize_resources`: url KHÔNG thuộc allowlist tên
        miền bị BỎ CẢ MỤC. Xem docstring app/resources.py cho lý
        do — tóm tắt: LLM sinh url là đoán chuỗi, domain bịa là rủi ro thật.

        resources rỗng KHÔNG phải lỗi (lý thuyết vẫn dùng được) → không raise,
        khác với theoryMarkdown rỗng.
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_lesson_theory_prompt(
            job_category, level, lesson_title, focus_criteria, weaknesses)

        # F22 — lượt gọi DUY NHẤT hoãn ghi nhận (defer_report): số liệu đáng giá ở
        # đây không chỉ là token mà còn là "AI bịa tên miền bao nhiêu lần" (allowlist
        # F15 hiện loại URL trong IM LẶNG — nếu Gemini bịa domain 90% số lần thì
        # không ai biết). Con số đó chỉ có SAU khi parse, nên phải hoãn.
        # try/finally BẮT BUỘC: parse hỏng thì token vẫn đã bị đốt, vẫn phải ghi.
        response = await self._generate(
            "generate_lesson_theory",
            defer_report=True,
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.5,  # nội dung giảng dạy — có ví dụ, không quá tất định
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "theoryMarkdown": {"type": "string"},
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
                    },
                    "required": ["theoryMarkdown"],
                },
            ),
        )

        url_meta: dict | None = None
        try:
            text = (response.text or "").strip()
            try:
                data = json.loads(text)
            except json.JSONDecodeError:
                raise ValueError(f"LLM sinh lý thuyết trả về JSON không hợp lệ: {text[:200]}")

            theory = str(data.get("theoryMarkdown", "")).strip()
            if not theory:
                raise ValueError("LLM không trả về nội dung lý thuyết hợp lệ.")

            resources = sanitize_resources(data.get("resources"))
            url_meta = count_rejected_urls(data.get("resources"))
            return theory, resources
        finally:
            await report_usage("generate_lesson_theory", settings.gemini_model,
                               response, meta=url_meta)

    async def summarize_roadmap(self, job_category: str, level: str,
                                criteria_progress: list[dict]) -> dict:
        """
        BC13/D20 — tổng kết roadmap: mạnh/yếu/cải thiện + nhận xét chung.

        Trả về dict:
          { "strengths": [str], "weaknesses": [str], "improvements": [str],
            "overallComment": str }
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_summarize_roadmap_prompt(job_category, level, criteria_progress)

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
                                criteria_scores: list[dict]) -> dict:
        """
        BC10 — nhận xét chung 1 buổi luyện B2C (sync, best-effort, KHÔNG ghi DB).

        criteria_scores: list dict từ Interview gửi qua, mỗi phần tử
          { "name": str, "percentage": float, "needsImprovement": bool }

        Trả về dict: { "overallComment": str }
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_summarize_session_prompt(job_category, overall_score, criteria_scores)

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
                          criteria: list[dict]) -> dict:
        """Phỏng vấn THÍCH ỨNG — quyết định hành động kế tiếp (sync, stateless, KHÔNG ghi DB).

        Trả về dict: { "action": str, "nextQuestion": str|None, "reason": str|None }
          action ∈ {follow_up, clarify, new_question, end}; nextQuestion None ⇔ end.

        temperature=0.3: bám sát câu trả lời/năng lực nhưng câu hỏi tự nhiên hơn chấm điểm
        (0.0) — thấp hơn sinh câu hỏi tự do (0.7) vì phải nhắm đúng câu trả lời + tiêu chí.
        """
        # F21 — nạp mảnh prompt admin đã tuỳ biến (no-op nếu cache còn hạn / registry tắt).
        await prompt_registry.refresh_if_stale()
        prompt = build_decide_next_prompt(
            job_category, current_question, transcript, history,
            asked_count, follow_up_count, max_questions, max_follow_ups, criteria)

        response = await self._generate(
            "decide_next",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.3,
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "action": {"type": "string"},
                        "nextQuestion": {"type": "string"},
                        "reason": {"type": "string"},
                    },
                    "required": ["action"],
                },
            ),
        )

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

        reason = str(data.get("reason", "") or "").strip() or None
        return {
            "action": action,
            "nextQuestion": next_q or None,   # end → None
            "reason": reason,
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
