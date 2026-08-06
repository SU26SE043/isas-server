<!--
Checklist này KHÔNG phải nghi thức — mỗi ô dưới đây tương ứng với một sự cố ĐÃ
XẢY RA THẬT trên repo này. Xoá mục không liên quan, đừng tick bừa.
-->

## Làm gì + vì sao

<!-- 2-3 câu. "Vì sao" quan trọng hơn "làm gì" — diff đã nói làm gì rồi. -->

## Xác minh

- [ ] `dotnet build` — **0 error**
- [ ] `dotnet test` — số test: `____` (ghi cả tổng và phần tăng, vd `1737 (+12)`)
- [ ] `pytest` — số test: `____` *(bắt buộc nếu chạm `src/services/Isas.AIService/`)*
- [ ] `dotnet ef migrations has-pending-model-changes` = **No changes** (mọi service bị chạm)
- [ ] Có hành vi mới → đã **mutation-check** (vô hiệu guard trong production → test tương ứng
      phải chuyển ĐỎ). Bắt buộc với task **tiền/bảo mật**.
      ⚠ Mutation ra XANH phải ĐIỀU TRA, không được nhận là "đã phủ".
      ⚠ Xác nhận mutation THẬT SỰ vào code (so hash) và **biên dịch được** —
      ĐỎ do lỗi biên dịch/cascade không phải bằng chứng.

## Migration

- [ ] **PR này không có migration nào** → bỏ qua cả mục này

Nếu có:

- Tên migration: `____`
- [ ] Đã đọc file migration **BẰNG MẮT** — test .NET KHÔNG phải bằng chứng
      (SQLite/`EnsureCreated` **bỏ qua migration** ⇒ backfill/rename/raw SQL không được test nào phủ)
- [ ] Mọi `migrationBuilder.Sql()` kết thúc bằng `;`
      *(thiếu `;` vẫn chạy được với `ef database update` nhưng **vỡ idempotent script** lúc deploy)*
- [ ] Loại: `additive` / `destructive`
      *(destructive = `DropColumn` · `DropTable` · `Rename*` · `Alter*` · raw `Sql()`)*
- [ ] Nếu **destructive**: đã tách **expand/contract 2 PR** chưa? Nếu chưa → nói rõ vì sao
      *(drop cột mà code đang chạy vẫn map = mọi request `42703 column does not exist` → 500)*
- [ ] Đã **apply lên prod** chưa? `chưa` / `rồi (ngày ____)`
- [ ] ⚠ **Migration phải đi TRƯỚC hoặc CÙNG lúc deploy, KHÔNG được đi sau.**
      CI deploy ngay khi merge ⇒ merge mà chưa apply là tự tạo sự cố
      (đã xảy ra **2 lần**: 02/08 và 05/08). Ai merge phải có mặt để apply.
- [ ] Preflight read-only đã chạy trên dữ liệu thật (CHECK/UNIQUE/NOT NULL mới có thể abort)

## Deploy

- [ ] **PR này không đổi gì về deploy** → bỏ qua cả mục này

Nếu có:

- Env mới: `____`
- [ ] Đã thêm vào `deploy/compose.yaml`
- [ ] Đã thêm vào `.env.example`
- [ ] Đã thêm vào `.env` **trên server**
- [ ] ⚠ Đã sửa **CẢ HAI** compose (repo **và** `~/docker/main/docker-compose.yml` trên server)
      — hai file đã trôi khỏi nhau theo cả hai chiều và từng để lại bug thật
- [ ] Cần rebuild image: `____`
      ⚠ **CI KHÔNG build AIService** — nếu chạm `Isas.AIService/` phải rebuild tay
- [ ] Cờ bật/tắt mặc định là gì, và **rollback** bằng cách nào (không cần deploy lại được không?)

## Tên nhánh

- [ ] Tên nhánh **khớp nội dung PR**

⚠ Prefix `docs/` hoặc `chore/` mà PR đụng file production (`.cs` · `.py` · `Dockerfile` ·
`ci.yml` · `compose.yaml` · `Migrations/`) → **đổi tên nhánh trước khi merge**.
Tiền lệ: PR #143 mang nhánh `docs/…` nhưng 8/9 file là code + hạ tầng — người duyệt
đọc tên nhánh rồi lướt qua là chuyện rất dễ xảy ra.

## Còn lại / chưa verify được

<!--
Ghi thẳng ra, đừng im lặng: cái gì L3 chưa chạy, cái gì cần team chốt,
đánh đổi nào đã CỐ Ý chấp nhận. Người duyệt cần biết ranh giới của bằng chứng.
-->
