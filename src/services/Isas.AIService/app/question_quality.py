"""Các kiểm tra thuần cho vòng chất lượng câu hỏi."""
from __future__ import annotations


def coverage_defects(target_criteria: list[list[str]] | None, criteria: list[dict] | None) -> list[str]:
    if target_criteria is None or not criteria:
        return []
    known = {str(c.get("criterionId", "")).strip(): str(c.get("name", "")).strip()
             for c in criteria if isinstance(c, dict)}
    covered = {criterion_id for targets in target_criteria for criterion_id in targets}
    missing = [name for criterion_id, name in known.items()
               if criterion_id and name and criterion_id not in covered]
    if not missing:
        return []
    return ["Các tiêu chí sau chưa có câu hỏi nào nhắm tới: " + ", ".join(missing)
            + ". Hãy sửa nội dung câu hỏi để phủ từng tiêu chí, không gắn nhãn bừa."]
