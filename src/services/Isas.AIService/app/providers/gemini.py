import json
from google import genai
from google.genai import types

from app.config import settings
from app.prompts import (
    build_prompt, build_scoring_prompt, build_criteria_prompt,
    build_cv_analysis_prompt,
    build_roadmap_prompt, build_lesson_theory_prompt, build_summarize_roadmap_prompt,
    build_summarize_session_prompt, build_decide_next_prompt,
)
from app.providers.base import QuestionProvider


class GeminiProvider(QuestionProvider):
    def __init__(self) -> None:
        self._client = genai.Client(api_key=settings.gemini_api_key)

    async def generate(self, job_category: str, cv_text: str | None,
                       jd_text: str | None) -> list[str]:
        prompt = build_prompt(job_category, cv_text, jd_text, settings.question_count)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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

        return questions[:settings.question_count]

    async def suggest_criteria(self, job_category: str, jd_text: str | None,
                               criteria_text: str | None, count: int) -> list[dict]:
        """Đề xuất bộ tiêu chí CÓ CẤU TRÚC (C8) — weight chuẩn hoá tổng = 1."""
        prompt = build_criteria_prompt(job_category, jd_text, criteria_text, count)
        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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
                    temperature: float = 0.0) -> list[dict]:
        """
        Chấm 1 câu trả lời theo rubric.

        criteria: list dict từ C# gửi qua, mỗi phần tử có
          { criterionId, name, description, maxScore, weight,
            levels: [{score, descriptor}], anchors?: [{score, exampleAnswer}] }

        temperature (E10 — self-consistency): nhiệt độ sinh. Attempt 1 = 0.0 (tái lập); attempt 2..N
          = SelfConsistencyTemperature (>0) để tạo dao động THẬT giữa các lần chấm → .NET đo spread
          (max−min) → gắn cờ needs_review khi phân tán. Mặc định 0.0 (giữ hành vi cũ / worker cũ).

        Trả về: list dict (E9 — neo theo mức)
          [ { "criterionId": str, "score": float, "levelMatched": int, "reasoning": str }, ... ]
        với score == levelMatched (score luôn = điểm của 1 mức HỢP LỆ của tiêu chí).
        """
        # Map criterionId -> maxScore + tập điểm mức HỢP LỆ (chấp cả key hoa/thường).
        # Nguồn mức: levels C# gửi (rubric_levels khai hoặc dải mặc định 0..maxScore).
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

        prompt = build_scoring_prompt(question, transcript, job_category, criteria)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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
                        }
                    },
                    "required": ["scores"],
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

        return results

    async def generate_roadmap(self, job_category: str, level: str,
                               weaknesses: list[dict] | None,
                               cv_text: str | None) -> list[dict]:
        """
        BC13/D20 — sinh cấu trúc roadmap ôn tập (sync, stateless, KHÔNG ghi DB).

        Trả về: list dict milestone
          [ { "title": str, "focusCriteria": [str], "lessons": [{"title": str}] }, ... ]
        """
        prompt = build_roadmap_prompt(job_category, level, weaknesses, cv_text)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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
                                     weaknesses: list[str] | None) -> str:
        """BC13/D20 — sinh nội dung lý thuyết (Markdown, tiếng Việt) cho 1 lesson."""
        prompt = build_lesson_theory_prompt(
            job_category, level, lesson_title, focus_criteria, weaknesses)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.5,  # nội dung giảng dạy — có ví dụ, không quá tất định
                response_mime_type="application/json",
                response_schema={
                    "type": "object",
                    "properties": {
                        "theoryMarkdown": {"type": "string"},
                    },
                    "required": ["theoryMarkdown"],
                },
            ),
        )

        text = (response.text or "").strip()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            raise ValueError(f"LLM sinh lý thuyết trả về JSON không hợp lệ: {text[:200]}")

        theory = str(data.get("theoryMarkdown", "")).strip()
        if not theory:
            raise ValueError("LLM không trả về nội dung lý thuyết hợp lệ.")

        return theory

    async def summarize_roadmap(self, job_category: str, level: str,
                                criteria_progress: list[dict]) -> dict:
        """
        BC13/D20 — tổng kết roadmap: mạnh/yếu/cải thiện + nhận xét chung.

        Trả về dict:
          { "strengths": [str], "weaknesses": [str], "improvements": [str],
            "overallComment": str }
        """
        prompt = build_summarize_roadmap_prompt(job_category, level, criteria_progress)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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
        prompt = build_summarize_session_prompt(job_category, overall_score, criteria_scores)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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
        prompt = build_decide_next_prompt(
            job_category, current_question, transcript, history,
            asked_count, follow_up_count, max_questions, max_follow_ups, criteria)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
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