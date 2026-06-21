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
        f"Hãy tạo đúng {count} câu hỏi phỏng vấn bằng tiếng Việt, "
        "đi từ cơ bản đến nâng cao.",
    ]

    # Thứ tự ưu tiên định hướng NỘI DUNG câu hỏi: JD > CV > JobCategory.
    # Lưu ý: JobCategory ({role}) luôn là vị trí ứng viên đang luyện và là
    # cơ sở để chấm điểm, nên câu hỏi phải giữ trọng tâm quanh vị trí này.
    if jd_text:
        # Có JD: JD dẫn nội dung, nhưng vẫn neo về vị trí {role}.
        parts.append(
            f"ĐỊNH HƯỚNG CHÍNH — Bám sát JD dưới đây để ra nội dung câu hỏi, "
            f"nhưng giữ trọng tâm phù hợp với vị trí {role} mà ứng viên đang luyện. "
            "Câu hỏi phải kiểm tra đúng năng lực mà JD đòi hỏi:\n"
            f"---JD---\n{jd_text}\n---"
        )
        if cv_text:
            # Có cả CV: dùng CV để cá nhân hóa, trong khung JD + vị trí.
            parts.append(
                "Kết hợp CV của ứng viên dưới đây để cá nhân hóa câu hỏi "
                "(liên hệ kinh nghiệm, dự án của họ với yêu cầu trong JD):\n"
                f"---CV---\n{cv_text}\n---"
            )
    elif cv_text:
        # Không có JD, chỉ có CV: CV dẫn nội dung, trong phạm vi vị trí {role}.
        parts.append(
            f"ĐỊNH HƯỚNG CHÍNH — Dựa vào CV của ứng viên dưới đây để cá nhân hóa "
            f"câu hỏi (hỏi sâu về kinh nghiệm, dự án, kỹ năng cụ thể trong CV), "
            f"trong phạm vi năng lực của vị trí {role}:\n"
            f"---CV---\n{cv_text}\n---"
        )
    else:
        # Không có CV lẫn JD: chỉ còn JobCategory làm kim chỉ nam.
        parts.append(
            f"ĐỊNH HƯỚNG CHÍNH — Không có CV/JD cụ thể. Hãy tạo câu hỏi phỏng vấn "
            f"tổng quát nhưng SÁT với năng lực cốt lõi của vị trí {role}. "
            "Mọi câu hỏi phải xoay quanh kỹ năng và kiến thức đặc thù của vị trí này."
        )

    parts.append(
        "CHỈ trả về JSON hợp lệ theo đúng định dạng, không thêm giải thích, "
        'không markdown: {"questions": ["câu 1", "câu 2", ...]}'
    )
    return "\n\n".join(parts)


def build_scoring_prompt(question: str, transcript: str,
                         job_category: str, criteria: list[dict]) -> str:
    # Dựng phần mô tả rubric từ criteria C# gửi sang.
    lines = []
    for c in criteria:
        cid = c.get("criterionId") or c.get("CriterionId")
        name = c.get("name") or c.get("Name")
        desc = c.get("description") or c.get("Description") or ""
        mx = c.get("maxScore") or c.get("MaxScore") or 5
        lines.append(f'- criterionId="{cid}" | Tiêu chí: {name} | Thang: 0-{mx} | {desc}')
    rubric_block = "\n".join(lines)

    return f"""Bạn là giám khảo phỏng vấn cho vị trí {job_category}.
Chấm câu trả lời của ứng viên theo từng tiêu chí trong rubric dưới đây.

CÂU HỎI:
{question}

CÂU TRẢ LỜI CỦA ỨNG VIÊN (đã chuyển từ giọng nói sang văn bản):
{transcript}

RUBRIC (chấm mỗi tiêu chí theo đúng thang điểm của nó):
{rubric_block}

YÊU CẦU:
- Chấm ĐỦ tất cả tiêu chí, mỗi tiêu chí một điểm số nguyên hoặc thập phân trong thang cho phép.
- Với mỗi tiêu chí, nêu lý do ngắn gọn (1-2 câu) bằng tiếng Việt, dựa trên nội dung câu trả lời.
- Dùng đúng criterionId được cung cấp, KHÔNG tự tạo id mới.
- Nếu câu trả lời trống hoặc lạc đề, cho điểm thấp tương ứng và nêu rõ lý do.
- Chấm khách quan theo bằng chứng trong câu trả lời, không suy diễn ngoài nội dung."""