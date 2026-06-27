# ISAS — Business Rules (đã tách theo service)

> Quy tắc nghiệp vụ + state machine đã **chuyển vào doc của từng service** trong `docs/services/`:
>
> - **Engine phỏng vấn** (state machine session/answer, chấm dần, republisher, idempotency, rubric, sinh câu hỏi, xác thực) → [services/interview.md](services/interview.md)
> - **Campaign** (lifecycle Draft→Active→Closed→Archived, distribution link "1 lần nộp", ranking/pass-fail) → [services/campaign.md](services/campaign.md)
> - **Payment & Credit** (vòng đời đơn, webhook PayOS idempotent, tiêu/cộng credit) → [services/payment.md](services/payment.md)
> - **AI reliability** (phân loại lỗi, temperature=0, chống ảo giác chấm) → [services/ai.md](services/ai.md)
