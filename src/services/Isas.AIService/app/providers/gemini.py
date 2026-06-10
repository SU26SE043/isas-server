import json
from google import genai
from google.genai import types

from app.config import settings
from app.prompts import build_prompt
from app.providers.base import QuestionProvider


class GeminiProvider(QuestionProvider):
    def __init__(self) -> None:
        # API key tự lấy từ GEMINI_API_KEY env, nhưng truyền tường minh cho chắc
        self._client = genai.Client(api_key=settings.gemini_api_key)

    async def generate(self, job_category: str, cv_text: str | None,
                       jd_text: str | None) -> list[str]:
        prompt = build_prompt(job_category, cv_text, jd_text, settings.question_count)

        # SDK mới: dùng .aio cho async
        response = await self._client.aio.models.generate_content(
            model=settings.gemini_model,
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.7,
                response_mime_type="application/json",  # ép trả JSON
            ),
        )

        text = (response.text or "").strip()
        data = json.loads(text)
        questions = data.get("questions", [])

        if not isinstance(questions, list) or not questions:
            raise ValueError("LLM không trả về danh sách câu hỏi hợp lệ.")

        return [str(q) for q in questions]