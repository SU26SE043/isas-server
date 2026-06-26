# ISAS — Cơ sở dữ liệu (đã tách theo service)

> Quy ước DB chung (snake_case, enum-string, **DB-per-service**, ref lỏng): [architecture.md](architecture.md) §5.
> Schema của từng service nằm trong `docs/services/`:
>
> | DB | Service | Doc |
> |---|---|---|
> | `isas` | Auth | [services/auth.md](services/auth.md) |
> | `isas_interview` | Interview (engine) | [services/interview.md](services/interview.md) |
> | `isas_campaign` | Campaign 🟡 | [services/campaign.md](services/campaign.md) |
> | `isas_payment` | Payment 🟡 | [services/payment.md](services/payment.md) |
