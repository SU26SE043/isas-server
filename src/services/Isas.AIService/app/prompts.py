CATEGORY_NAMES = {
    "BA": "Business Analyst",
    "BE": "Backend Developer",
    "FE": "Frontend Developer",
}


def build_prompt(job_category: str, cv_text: str | None,
                 jd_text: str | None, count: int) -> str:
    role = CATEGORY_NAMES.get(job_category.upper(), job_category)

    parts = [
        f"Bạn là một interviewer chuyên nghiệp cho vị trí {role}.",
        f"Hãy tạo đúng {count} câu hỏi phỏng vấn bằng tiếng Việt.",
        "Câu hỏi phải đi từ cơ bản đến nâng cao, phù hợp với vị trí.",
    ]

    if cv_text:
        parts.append(
            "Dựa vào CV của ứng viên dưới đây để cá nhân hóa câu hỏi "
            f"(hỏi về kinh nghiệm, dự án, kỹ năng cụ thể):\n---CV---\n{cv_text}\n---"
        )
    if jd_text:
        parts.append(
            "Bám sát yêu cầu công việc (JD) dưới đây:\n"
            f"---JD---\n{jd_text}\n---"
        )

    parts.append(
        "CHỈ trả về JSON hợp lệ theo đúng định dạng, không thêm giải thích, "
        'không markdown: {"questions": ["câu 1", "câu 2", ...]}'
    )
    return "\n\n".join(parts)