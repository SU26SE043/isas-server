# ISAS — Cơ sở dữ liệu (đã tách theo service)

> Quy ước DB chung (snake_case, enum-string, **DB-per-service**, ref lỏng): [architecture.md](architecture.md) §5.
> Schema của từng service nằm trong `docs/services/`:
>
> | DB | Service | Doc (số bảng live) |
> |---|---|---|
> | `isas` | Auth (Identity + Org) | [services/auth.md](services/auth.md) — **11 bảng** |
> | `isas_interview` | Interview (engine B2C+B2B) | [services/interview.md](services/interview.md) — **16 bảng** (+ `knowledge_sources` grounding D27) |
> | `isas_campaign` | Campaign (B2B) | [services/campaign.md](services/campaign.md) — **12 bảng** |
> | `isas_payment` | Payment (credit/PayOS) | [services/payment.md](services/payment.md) — **10 bảng** |
>
> **✅ Cả 4 DB đã deploy live** (container `postgres-main`, PostgreSQL 18, trên server) — migration squash → 1 `InitialCreate`/service (giữ rule **no-auto-migrate**: apply tay/pipeline TRƯỚC deploy).
> **Review kiến trúc DB + backlog hardening** (CHECK/outbox/index/FK/scale): [tasks.md](tasks.md) **§S6** (task `DB1–DB19`).
