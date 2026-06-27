from pydantic import BaseModel


class GenerateQuestionsRequest(BaseModel):
    jobCategory: str            # BA | BE | FE
    cvText: str | None = None
    jdText: str | None = None


class GenerateQuestionsResponse(BaseModel):
    questions: list[str]