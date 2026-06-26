# ISAS — Progress / Handoff

> Trạng thái hiện tại + bước kế tiếp, để phiên/người mới nối tiếp nhanh. Kế hoạch đầy đủ & phân việc: [work-division.md](work-division.md). Lý do quyết định: [decisions.md](decisions.md).
> **Cập nhật mỗi khi đổi trạng thái** (tan ca). Cập nhật lần cuối: **2026-06-27**.

## Pha hiện tại
**Design docs + bước nền — đã MERGE vào `main`.** Toàn bộ kiến trúc B2B + thanh toán (D1–**D15**, gồm **D15 ví B2C personal credit** `credit_accounts(owner_type)`) đã ghi vào `docs/` và merge: **PR #19** (code) + **PR #20** (docs). **Đã code & merge:** engine `campaign_id` (nullable) trên `practice_sessions` + `rubric_criteria` (migration `AddCampaignIdToEngine`) — nền cho **I1/D2** ([tasks.md](tasks.md)).
**Mới nhất (2026-06-27):** AIService đã **sẵn sàng Docker** — thêm `Dockerfile` + vá `requirements.txt` (thiếu `aio-pika`/`boto3`/`aiohttp`, build + import đã verify) — và chuẩn bị bộ artifact deploy (server compose + `.env` + `seaweed-s3.json`, Mac compose) theo [../DEPLOYMENT.md](../DEPLOYMENT.md). *2 file AIService (`Dockerfile`, `requirements.txt`) **chưa commit**.*

## Trạng thái branch (remote)
| Branch | Nội dung | Trạng thái |
|---|---|---|
| `main` (đang checkout) | engine B2C + **`campaign_id`** + **docs B2B (D1–D15)** đã merge (PR #19, #20); AIService Dockerfile/requirements **chưa commit** | ✅ (trừ 2 file AIService) |
| `feature/candidate-b2c` · `docs/b2b-design` | nguồn của PR #19/#20 | ✅ đã merge vào main |
| `features/campaign-service` | CampaignService (CRUD + JD/Criteria + PdfPig) | 🟡 còn 6 bug (xem [services/campaign.md](services/campaign.md)) |
| `features/payment-b2c` | PaymentService (Order/Package/PayOS) | 🟡 thiếu: org-credit, reserve/consume, postpaid, active-polling |

## Vấn đề đã biết / cần xác minh
- ⚠ **Trần `orderCode` của PayOS** chưa verify (quyết định D12 phụ thuộc cái này).
- CampaignService: 6 bug (URL-vs-key, GET không lọc, AntiCheat ghi đè, download zip, message DOCX, Authorize comment).
- PaymentService trên branch theo **credit cá nhân (`user_id`)** — cần refactor sang **org-credit + reserve/consume + postpaid** theo doc.
- **AIService** (đã soi code): 🔴 endpoint `/ai/**` public không auth · 🔴 Whisper `large-v3` CPU quá chậm (trần thông lượng; đã thêm `WHISPER_MODEL` env để hạ `base/small`) · 🔴 prompt injection vào bộ chấm · 🔴 `nack` không DLQ. Chi tiết [services/ai.md](services/ai.md) §Vấn đề đã biết. *(Lõi xử-lý-lỗi/validate tốt — giữ.)*
- **Deploy/Docker (mới, 2026-06-27):** ✅ AIService `requirements.txt` thiếu `aio-pika/boto3/aiohttp` → **đã fix** + thêm `Dockerfile` (build & import verify). · 🟡 **DEPLOYMENT.md §4 seaweed thiếu `-s3.config` + mount `seaweed-s3.json`** — lệch container thật (đang bật auth `admin/admin123456`); cần thêm flag+mount vào compose & doc. · 🟡 **Redis** khai trong compose/architecture nhưng AuthService **không dùng** (refresh token ở Postgres) → cân nhắc bỏ hoặc sửa doc.
- `AGENTS.md` đang **tạm trong `docs/`** (theo convention nên đưa ra gốc khi chốt bố cục) · chưa có CI cho doc.

## Bước tiếp theo (thứ tự đề xuất)
1. **Commit AIService Docker** (`Dockerfile` + `requirements.txt`, đang dở trên main) + đồng bộ [../DEPLOYMENT.md](../DEPLOYMENT.md) (seaweed `-s3.config` + mount `seaweed-s3.json`; gỡ/sửa Redis). *(docs B2B + campaign_id đã merge — PR #19/#20, không còn ở bước này.)*
2. **Phase 0 — Foundation/Init** (TRƯỚC feature): 6 service chạy từ máy sạch · test mẫu pass cho **Campaign + Payment** (đang thiếu) · lệnh chuẩn hoá `make setup/test/check` · readiness 4 điều kiện xanh · commit checkpoint. Chi tiết [work-division.md](work-division.md) §5.
3. **S1**: Auth thêm Organization + org-role; PaymentService refactor org-credit + reserve/consume + postpaid.
4. **S2**: hoàn thiện CampaignService (fix 6 bug) + tiêu chí text→cấu trúc + soft-delete/audit.
5. **S3**: Distribution (magic-link) + InterviewService nhận `campaign_id` + create-or-get.
6. **S4**: chấm theo tiêu chí campaign + event `SessionScored` + `campaign_rankings` + export.

> Quy trình **vào ca / tan ca**: xem [AGENTS.md](AGENTS.md).
