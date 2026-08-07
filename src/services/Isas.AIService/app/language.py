"""Shared language policy for bilingual AI output.

Vietnamese is deliberately the fail-safe default: older callers do not send a
language and must keep their exact existing behaviour.
"""

import os
import logging

VI = "vi"
EN = "en"
_logger = logging.getLogger(__name__)


def normalize(language: str | None) -> str:
    allowed = {item.strip() for item in os.getenv("BILINGUAL_ALLOWED_LANGUAGES", VI).split(",")}
    if language == EN and EN not in allowed:
        _logger.warning("Bilingual request downgraded to Vietnamese because BILINGUAL_ALLOWED_LANGUAGES excludes en")
    return EN if language == EN and EN in allowed else VI


def output_directive(language: str | None) -> str:
    return f"Toàn bộ nội dung sinh ra PHẢI bằng {field_lang(language)}."


def field_lang(language: str | None) -> str:
    return "English" if normalize(language) == EN else "tiếng Việt"


def speech_rate_reference(language: str | None) -> str:
    if normalize(language) == EN:
        return "120–180 words/min"
    return "180–320 âm tiết/phút"


def rate_unit(language: str | None) -> str:
    return " words/min" if normalize(language) == EN else " âm tiết/phút"


def per100_unit(language: str | None) -> str:
    return " per 100 words" if normalize(language) == EN else " lần/100 âm tiết"


def lesson_example_heading(language: str | None) -> str:
    """Nhãn mục ví dụ trong bài giảng roadmap (server tự ghép, KHÔNG do model sinh)."""
    return "Worked example" if normalize(language) == EN else "Ví dụ minh hoạ"


def lesson_mistakes_heading(language: str | None) -> str:
    """Nhãn mục lỗi thường gặp trong bài giảng roadmap (server tự ghép)."""
    return ("Common mistakes in interview answers" if normalize(language) == EN
            else "Lỗi thường gặp khi trả lời phỏng vấn")
