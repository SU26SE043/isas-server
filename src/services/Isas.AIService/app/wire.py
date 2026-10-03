"""Đọc khoá JSON mà .NET gửi sang theo HAI quy ước viết hoa/thường.

🔴 Vì sao cần: job chấm đi qua RabbitMQ được `ScoringJobPublisher.cs` tuần tự hoá bằng
`JsonSerializer.Serialize(job)` KHÔNG kèm options ⇒ `JsonSerializerOptions.Default` = **PascalCase**
(`{"Score":0,"Descriptor":"…"}`). Đường HTTP của ASP.NET Core thì dùng Web defaults = camelCase
(`{"score":0,"descriptor":"…"}`), và chấm thử (CAMP-19) đi đường đó. Chỉ đọc một kiểu thì field
chết IM LẶNG ở đường kia: không lỗi, không cảnh báo — chỉ là một giá trị None.

Đã cắn thật (E9, sống từ 2026-07-12): mốc điểm trên hàng đợi bị đọc thành None ⇒ prompt chấm in
"• Mức None: " không mô tả, `score()` rơi về dải 0..maxScore ⇒ AI tự cho điểm ngoài mốc HR khai
còn chấm thử thì vẫn thấy mốc — CHẤM THỬ ≠ CHẤM THẬT, đúng thứ CAMP-19 cấm.
"""
from typing import Any


def wire_get(d: Any, camel: str, pascal: str) -> Any:
    """Lấy ``d[camel]``, khuyết thì ``d[pascal]``; ``d`` không phải dict ⇒ None.

    Kiểm **``is None``**, KHÔNG kiểm truthiness — đúng mẫu `temperature` ở `worker.py`.
    ``d.get(camel) or d.get(pascal)`` sẽ coi mốc **0 điểm** là "không có" và đi tra khoá kia: với
    dict camelCase (đường chấm thử) kết quả là None, tức mốc 0 biến mất khỏi prompt lẫn tập mốc để
    snap. Mốc 0 là bắt buộc (CAMP-17), nên đây không phải ca biên.
    """
    if not isinstance(d, dict):
        return None
    value = d.get(camel)
    if value is None:
        value = d.get(pascal)
    return value
