import json
from google import genai
from google.genai import types

from app.config import settings
from app.prompts import build_prompt, build_scoring_prompt
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

    async def score(self, question: str, transcript: str,
                    job_category: str, criteria: list[dict]) -> list[dict]:
        """
        Chấm 1 câu trả lời theo rubric.

        criteria: list dict từ C# gửi qua, mỗi phần tử có
          { criterionId, name, description, maxScore, weight }

        Trả về: list dict
          [ { "criterionId": str, "score": float, "reasoning": str }, ... ]
        """
        # Map criterionId -> maxScore để validate sau (chấp cả 2 kiểu key hoa/thường).
        max_by_id: dict[str, int] = {}
        for c in criteria:
            cid = str(c.get("criterionId") or c.get("CriterionId"))
            max_by_id[cid] = int(c.get("maxScore") or c.get("MaxScore") or 5)

        prompt = build_scoring_prompt(question, transcript, job_category, criteria)

        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.0,  # chấm cần nhất quán, không sáng tạo
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
                                    "reasoning": {"type": "string"},
                                },
                                "required": ["criterionId", "score", "reasoning"],
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

            # Kẹp điểm trong [0, maxScore] phòng LLM trả ngoài thang.
            score = float(item.get("score", 0))
            score = max(0.0, min(score, float(max_by_id[cid])))

            results.append({
                "criterionId": cid,
                "score": score,
                "reasoning": str(item.get("reasoning", "")).strip(),
            })

        # Đảm bảo chấm đủ mọi tiêu chí; thiếu cái nào -> coi như lỗi để retry.
        missing = set(max_by_id.keys()) - seen
        if missing:
            raise ValueError(f"LLM chấm thiếu tiêu chí: {missing}")

        return results