# ISAS — Progress / Handoff

> Trạng thái hiện tại + bước kế tiếp, để phiên/người mới nối tiếp nhanh. Kế hoạch đầy đủ & phân việc: [work-division.md](work-division.md). Lý do quyết định: [decisions.md](decisions.md).
> **Cập nhật mỗi khi đổi trạng thái** (tan ca). Cập nhật lần cuối: **2026-06-28**.

## Pha hiện tại
**Thiết kế (design docs) — gần xong.** Toàn bộ kiến trúc B2B + thanh toán đã chốt & ghi vào `docs/` (xem [decisions.md](decisions.md) D1–D15). **Bước nền B2B đầu tiên đã code:** engine InterviewService thêm cột `campaign_id` (nullable) vào `practice_sessions` + `rubric_criteria` (migration `AddCampaignIdToEngine`) — đúng D1, non-breaking cho B2C, là tiền đề cho **I1/D2** ([tasks.md](tasks.md)). ✅ đã commit + merge vào `features/campaign-service` (2026-06-27).

## Trạng thái branch (remote)
| Branch | Nội dung | Trạng thái |
|---|---|---|
| `dev` / `main` | engine B2C đang chạy (Auth, Interview, AI, Gateway) | ✅ |
| `features/campaign-service` | CampaignService (CRUD + JD/Criteria + PdfPig + publish/criteria/audit + AI) · **đã merge main** (2026-06-27) | 🟡 ✅ **đã commit & push, sync remote** (6 bug + soft-delete + lifecycle + snake_case + C8 publish/`campaign_criteria` + C10 `audit_logs` + AI `/suggest-criteria` live); còn distribution/ranking/org_id (xem [services/campaign.md](services/campaign.md)) |
| `features/payment-b2c` | PaymentService (Order/Package/PayOS) | 🟡 thiếu: org-credit, reserve/consume, postpaid, active-polling |
| `feature/candidate-b2c` | docs B2B + engine thêm `campaign_id` (nullable) | ✅ migration `AddCampaignIdToEngine` **đã commit + merge** vào `features/campaign-service` |

## Vấn đề đã biết / cần xác minh
- ⚠ **Trần `orderCode` của PayOS** chưa verify (quyết định D12 phụ thuộc cái này).
- ✅ **CampaignService: 6 bug ĐÃ FIX** (URL→key · GET lọc `employer_id` · AntiCheat `bool?` · download pdf+404 · message "Only PDF" · `[Authorize]`) **+ soft-delete (C9) + lifecycle (C7: publish + status transition) + C8 publish/`campaign_criteria` (Σweight=1) + C10 `audit_logs` + snake_case** — build sạch, **34 unit test pass**, `isas_campaign` migrate thật (4 bảng). **AI thật:** publish gọi AIService `POST /suggest-criteria` (Gemini), ✅ live HTTP OK. ⚠ container `aiapi` deploy qua `docker cp`+restart → **ephemeral**, permanent cần rebuild image. Còn: org_id (cần A1), distribution/ranking.
- PaymentService trên branch theo **credit cá nhân (`user_id`)** — cần refactor sang **org-credit + reserve/consume + postpaid** theo doc.
- **AIService** (đã soi code): 🔴 endpoint `/ai/**` public không auth · 🔴 Whisper `large-v3` CPU quá chậm (trần thông lượng) · 🔴 prompt injection vào bộ chấm · 🔴 `nack` không DLQ. Chi tiết [services/ai.md](services/ai.md) §Vấn đề đã biết. *(Lõi xử-lý-lỗi/validate tốt — giữ.)*
- `AGENTS.md` đang **tạm trong `docs/`** (theo convention nên đưa ra gốc khi chốt bố cục) · chưa có CI cho doc.

## Bước tiếp theo (thứ tự đề xuất)
1. **Commit docs** (branch riêng `docs/b2b-design` → PR vào `dev`).
2. **Phase 0 — Foundation/Init** (TRƯỚC feature): 6 service chạy từ máy sạch · test mẫu pass cho **Campaign + Payment** (đang thiếu) · lệnh chuẩn hoá `make setup/test/check` · readiness 4 điều kiện xanh · commit checkpoint. Chi tiết [work-division.md](work-division.md) §5.
3. **S1**: Auth thêm Organization + org-role; PaymentService refactor org-credit + reserve/consume + postpaid.
4. **S2**: hoàn thiện CampaignService (fix 6 bug) + tiêu chí text→cấu trúc + soft-delete/audit.
5. **S3**: Distribution (magic-link) + InterviewService nhận `campaign_id` + create-or-get.
6. **S4**: chấm theo tiêu chí campaign + event `SessionScored` + `campaign_rankings` + export.

> Quy trình **vào ca / tan ca**: xem [AGENTS.md](AGENTS.md).
