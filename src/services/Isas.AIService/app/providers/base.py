from abc import ABC, abstractmethod


class QuestionProvider(ABC):
    @abstractmethod
    async def generate(self, job_category: str, cv_text: str | None,
                       jd_text: str | None, count: int | None = None,
                       focus_criteria: list[str] | None = None) -> list[str]:
        ...