# ISAS — Hướng dẫn Deploy (2 host)

Kiến trúc tách **2 máy** nối nhau qua **Tailscale**:

- **Server (Linux)** — chạy data layer + các .NET service: `postgres`, `redis`, `seaweedfs`, `rabbitmq`, `authservice`, `interviewservice`, `campaignservice`, `paymentservice`, `gateway`. Đây cũng là đích CI/CD (`ci.yml` build **5** image → push GHCR → SSH deploy).
- **Mac (Docker)** — chạy **AIService** (Python): `aiservice-api` (FastAPI sinh câu hỏi) + `aiservice-worker` (consumer chấm điểm). Để Mac vì phần ML (Whisper) nặng.

> Tất cả liên lạc cross-host đi qua **IP Tailscale riêng tư**, không mở cổng public.

---

## 1. Sơ đồ liên lạc

```
SERVER (Linux)                              MAC (Docker)
┌──────────────────────────┐                ┌─────────────────────┐
│ postgres  redis          │                │ aiservice-api :8000 │◄─┐
│ seaweedfs :8333 ◄────────┼───────┐        │  (sinh câu hỏi)     │  │
│ rabbitmq  :5672 ◄────────┼─────┐ │        │                     │  │
│ interviewservice :5246 ◄─┼───┐ │ │        │ aiservice-worker    │  │
│ authservice              │   │ │ └────────│  - kéo audio (S3)   │  │
│ gateway   :5050          │   │ └──────────│  - chấm (Gemini)    │  │
│                          │   └────────────│  - callback kết quả │  │
│ gateway   ai-cluster ────┼────────────────┼─────────────────────┘  │
│ interview AiService:Base ┼────────────────┴────────────────────────┘
└──────────────────────────┘   (server → Mac:8000 để sinh câu hỏi)
```

**Mac → Server:** worker kéo job `rabbitmq:5672`, tải audio `seaweedfs:8333`, callback `interviewservice:5246`.
**Server → Mac:** gateway + interviewservice gọi `aiservice-api:8000` để sinh câu hỏi.

---

## 2. Yêu cầu trước

- [ ] **Tailscale** cài trên **cả** Server và Mac, cùng tailnet. Lấy IP: `tailscale ip -4`.
  - `<SERVER_TS_IP>` = IP Tailscale của server.
  - `<MAC_TS_IP>` = IP Tailscale của Mac.
- [ ] **Docker + Docker Compose** trên cả 2 máy.
- [ ] Firewall/Tailscale ACL: cổng `5672`, `8333`, `5246` (server) và `8000` (Mac) **chỉ** cho phép tailnet — không lộ public.

---

## 3. Secret phải KHỚP nhau

| Secret | Dùng ở | Quy tắc |
|---|---|---|
| `Jwt__Key` / `Jwt__Issuer` / `Jwt__Audience` | authservice ↔ interviewservice | **giống hệt** (Interview validate token do Auth phát) |
| `Internal__Token` ↔ `INTERNAL_TOKEN` | interviewservice ↔ aiservice-worker | **giống hệt** (xác thực callback chấm điểm) |
| SeaweedFS access/secret | interviewservice ↔ aiservice-worker | cùng giá trị (S3 dùng chung) |

> Giá trị thật để trong file `.env` cạnh compose trên server / Mac — **không** ghi vào file md này.

---

## 4. SERVER — `~/docker/main/compose.yaml`

```yaml
services:
  # ===== DATA LAYER =====
  postgres:
    image: postgres:18
    container_name: postgres-main
    restart: always
    environment:
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    ports:
      - "5432:5432"
    volumes:
      - postgres_main_data:/var/lib/postgresql
    networks: [isas-main-network]

  redis:
    image: redis:7
    container_name: redis-main
    restart: always
    ports:
      - "6379:6379"
    volumes:
      - redis_main_data:/data
    networks: [isas-main-network]

  seaweedfs:
    image: chrislusf/seaweedfs:latest
    container_name: seaweedfs-main
    restart: always
    # -s3.config bật S3 auth bằng file identities (seaweed-s3.json). THIẾU nó → S3 mở toang, key bị bỏ qua.
    command: "server -dir=/data -s3 -s3.port=8333 -s3.config=/etc/seaweedfs/s3.json -ip.bind=0.0.0.0"
    ports:
      - "8333:8333"   # S3 API (Mac kéo audio qua tailnet)
      - "8888:8888"   # filer
      - "9333:9333"   # master UI
    volumes:
      - seaweedfs_main_data:/data
      - ./seaweed-s3.json:/etc/seaweedfs/s3.json:ro   # identities S3 (access/secret key)
    networks: [isas-main-network]

  rabbitmq:
    image: rabbitmq:3-management
    container_name: rabbitmq-main
    restart: always
    environment:
      RABBITMQ_DEFAULT_USER: ${RABBITMQ_USER}
      RABBITMQ_DEFAULT_PASS: ${RABBITMQ_PASS}
    ports:
      - "5672:5672"    # AMQP (Mac worker consume qua tailnet)
      - "15672:15672"  # management UI
    networks: [isas-main-network]

  # ===== APP SERVICES =====
  isas.authservice:
    image: ghcr.io/su26se043/isas.authservice:main
    container_name: authservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=isas;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Jwt__Key=${JWT_KEY}
      - Jwt__Issuer=http://isas.authservice:8080
      - Jwt__Audience=http://isas.authservice:8080
      - Jwt__RefreshTokenDays=7
      - Jwt__AccessTokenMinutes=15
      - EmailSettings__Host=${SMTP_HOST}
      - EmailSettings__Port=${SMTP_PORT}
      - EmailSettings__Username=${SMTP_USER}
      - EmailSettings__Password=${SMTP_PASS}
      - EmailSettings__From=${SMTP_FROM}
      - Authentication__Google__ClientId=${GOOGLE_CLIENT_ID}
      - Authentication__Google__ClientSecret=${GOOGLE_CLIENT_SECRET}
      # Đăng nhập Google: callback 302 về FE kèm token ở fragment. Cả 2 URL lấy từ CONFIG SERVER
      # (không nhận đích từ client — nếu không là open-redirect làm rò token).
      - Frontend__BaseUrl=${FRONTEND_BASE_URL}
      - Gateway__PublicBaseUrl=${GATEWAY_PUBLIC_BASE_URL}
    expose: ["8080"]
    depends_on: [postgres, redis]
    networks: [isas-main-network]
    restart: unless-stopped

  isas.interviewservice:
    image: ghcr.io/su26se043/isas.interviewservice:main
    container_name: interviewservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=isas_interview;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Jwt__Key=${JWT_KEY}                         # KHỚP authservice
      - Jwt__Issuer=http://isas.authservice:8080
      - Jwt__Audience=http://isas.authservice:8080
      - Internal__Token=${INTERNAL_TOKEN}           # KHỚP aiservice-worker
      - SeaweedFS__ServiceURL=http://seaweedfs:8333
      - SeaweedFS__AccessKey=${S3_ACCESS_KEY}
      - SeaweedFS__SecretKey=${S3_SECRET_KEY}
      - SeaweedFS__BucketName=isas-files
      - SeaweedFS__ForcePathStyle=true
      - SeaweedFS__UseHttp=true
      - RabbitMQ__HostName=rabbitmq
      - RabbitMQ__UserName=${RABBITMQ_USER}
      - RabbitMQ__Password=${RABBITMQ_PASS}
      - AiService__BaseUrl=http://<MAC_TS_IP>:8000   # sinh câu hỏi chạy trên Mac
    ports:
      - "5246:8080"     # publish để Mac gọi callback /internal/... qua tailnet
    depends_on: [postgres, seaweedfs, rabbitmq]
    networks: [isas-main-network]
    restart: unless-stopped

  isas.campaignservice:
    image: ghcr.io/su26se043/isas.campaignservice:main
    container_name: campaignservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=isas_campaign;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Jwt__Key=${JWT_KEY}
      - Jwt__Issuer=http://isas.authservice:8080
      - Jwt__Audience=http://isas.authservice:8080
      - AiService__BaseUrl=http://<MAC_TS_IP>:8000
      - SeaweedFS__ServiceURL=http://seaweedfs:8333
      - SeaweedFS__AccessKey=${S3_ACCESS_KEY}
      - SeaweedFS__SecretKey=${S3_SECRET_KEY}
      - SeaweedFS__BucketName=isas-files
      - SeaweedFS__ForcePathStyle=true
      # RabbitMQ — Campaign publish email mời (D1) + cv-screening (C14) + consume ranking (E4).
      # THIẾU → POST /invitations 500 "endpoints not reachable" (bắt ở API sweep 2026-07-13).
      - RabbitMQ__HostName=rabbitmq
      - RabbitMQ__UserName=${RABBITMQ_USER}
      - RabbitMQ__Password=${RABBITMQ_PASS}
    expose: ["8080"]
    depends_on: [postgres, seaweedfs, rabbitmq]
    networks: [isas-main-network]
    restart: unless-stopped

  isas.paymentservice:
    image: ghcr.io/su26se043/isas.paymentservice:main
    container_name: paymentservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=isas_payment;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Jwt__Key=${JWT_KEY}
      - Jwt__Issuer=http://isas.authservice:8080
      - Jwt__Audience=http://isas.authservice:8080
      - Internal__Token=${INTERNAL_TOKEN}
      - RabbitMQ__HostName=rabbitmq
      - RabbitMQ__UserName=${RABBITMQ_USER}
      - RabbitMQ__Password=${RABBITMQ_PASS}
      - PayOS__ClientId=${PAYOS_CLIENT_ID}
      - PayOS__ApiKey=${PAYOS_API_KEY}
      - PayOS__ChecksumKey=${PAYOS_CHECKSUM_KEY}
      - PayOS__ReturnUrl=${PAYOS_RETURN_URL}     # BF3 — bắt buộc, PayOS reject tạo link nếu null
      - PayOS__CancelUrl=${PAYOS_CANCEL_URL}     # BF3 — bắt buộc
    ports:
      - "5271:8080"     # publish để webhook PayOS gọi vào (cần public URL/tunnel)
    depends_on: [postgres, rabbitmq]
    networks: [isas-main-network]
    restart: unless-stopped

  isas.gateway:
    image: ghcr.io/su26se043/isas.gateway:main
    container_name: gateway-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      # Chỉ override địa chỉ runtime; routing /api/v1 lấy từ appsettings.json trong image.
      - ReverseProxy__Clusters__auth-cluster__Destinations__auth-node-01__Address=http://isas.authservice:8080
      - ReverseProxy__Clusters__interview-cluster__Destinations__interview-node-01__Address=http://isas.interviewservice:8080
      - ReverseProxy__Clusters__campaign-cluster__Destinations__campaign-node-01__Address=http://isas.campaignservice:8080
      - ReverseProxy__Clusters__payment-cluster__Destinations__payment-node-01__Address=http://isas.paymentservice:8080
      # GEN-7 (2026-07-13): ai-cluster + /api/v1/ai route đã GỠ khỏi gateway (AI internal-only qua AiService:BaseUrl).
      # → không còn override ai-cluster address / ai OpenApi. Index ApiServices dồn lại (bỏ ai=cũ-index-1).
      - ApiServices__0__OpenApiUrl=http://isas.authservice:8080/openapi/v1.json
      - ApiServices__1__OpenApiUrl=http://isas.interviewservice:8080/openapi/v1.json
      - ApiServices__2__OpenApiUrl=http://isas.campaignservice:8080/openapi/v1.json
      - ApiServices__3__OpenApiUrl=http://isas.paymentservice:8080/openapi/v1.json
      - Gateway__Url=${GATEWAY_PUBLIC_URL}
      - Cors__AllowedOrigins__0=http://localhost:3000
      - Cors__AllowedOrigins__1=http://localhost:5173
      - Cors__AllowedOrigins__2=http://localhost:5174
      - Cors__AllowedOrigins__3=https://isas-web-client.vercel.app
      - Cors__AllowedOrigins__4=${GATEWAY_PUBLIC_URL}
    ports:
      - "5050:8080"
    depends_on: [isas.authservice, isas.interviewservice, isas.campaignservice, isas.paymentservice]
    networks: [isas-main-network]
    restart: unless-stopped

networks:
  isas-main-network:
    driver: bridge

volumes:
  postgres_main_data:
  redis_main_data:
  seaweedfs_main_data:
```

### Server `.env` (cạnh compose, `chmod 600`)

```env
POSTGRES_USER=admin
POSTGRES_PASSWORD=...
JWT_KEY=...
INTERNAL_TOKEN=...
S3_ACCESS_KEY=admin
S3_SECRET_KEY=...
RABBITMQ_USER=guest
RABBITMQ_PASS=guest
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USER=...
SMTP_PASS=...
SMTP_FROM=...
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
# Đăng nhập Google — bắt buộc nếu bật login-google (thiếu → callback trả 500 khi dựng URL đích).
# Redirect URI phải khai trên Google Cloud Console: ${GATEWAY_PUBLIC_BASE_URL}/auth/signin-google
FRONTEND_BASE_URL=https://<your-frontend>
GATEWAY_PUBLIC_BASE_URL=https://<your-tunnel>.trycloudflare.com/api/v1
GATEWAY_PUBLIC_URL=https://<your-tunnel>.trycloudflare.com
# PaymentService (PayOS) — bắt buộc để mua credit / webhook chạy
PAYOS_CLIENT_ID=...
PAYOS_API_KEY=...
PAYOS_CHECKSUM_KEY=...
# BF3 — bắt buộc: thiếu → POST /order 502 (PayOS reject "return_url null"). URL redirect sau thanh toán.
PAYOS_RETURN_URL=https://<your-frontend-or-tunnel>/payment/success
PAYOS_CANCEL_URL=https://<your-frontend-or-tunnel>/payment/cancel
```

### Server `seaweed-s3.json` (cạnh compose) — identities cho S3 auth
Seaweed bật auth bằng file này (`-s3.config` ở trên). `accessKey`/`secretKey` phải **khớp** `S3_ACCESS_KEY`/`S3_SECRET_KEY` trong `.env` **và** phía Mac (`aiservice-worker`).
```json
{
  "identities": [
    {
      "name": "admin",
      "credentials": [ { "accessKey": "admin", "secretKey": "<S3_SECRET_KEY>" } ],
      "actions": ["Admin", "Read", "Write", "List"]
    }
  ]
}
```

### Bring-up server (lần đầu — sau đó CI tự `pull && up`)

```bash
cd ~/docker/main
# tạo 4 DB (mỗi service 1 DB, __EFMigrationsHistory tách riêng)
docker compose up -d postgres
docker exec -it postgres-main psql -U admin -c \
  "CREATE DATABASE isas; CREATE DATABASE isas_interview; CREATE DATABASE isas_campaign; CREATE DATABASE isas_payment;"
# login GHCR + chạy
echo <GHCR_TOKEN> | docker login ghcr.io -u <github-user> --password-stdin
docker compose pull
docker compose up -d
docker compose logs -f isas.gateway
```

> **Migration (2026-07-13 — squash):** mỗi service đã gộp về **1 `InitialCreate`**. App **KHÔNG auto-migrate** → apply schema **THỦ CÔNG** lên DB **rỗng** (seed rubric B2C bake sẵn trong Interview):
> ```bash
> # cách 1 — có .NET SDK: mỗi service
> cd src/services/Isas.<Svc>
> dotnet ef database update --connection "Host=<server>;Port=5432;Database=<isas|isas_interview|isas_campaign|isas_payment>;Username=admin;Password=<pwd>"
> # cách 2 — không SDK: sinh SQL rồi psql
> dotnet ef migrations script -o init_<db>.sql   # (chạy nơi có SDK)
> docker exec -i postgres-main psql -U admin -d <db> < init_<db>.sql
> ```
> Đổi/reset schema → **drop & tạo lại DB** trước khi apply (squash chỉ sạch trên DB rỗng).

> **S6 hardening rounds — apply migration TĂNG DẦN (DB đã có data, KHÔNG drop):** dùng **idempotent script** (`dotnet ef migrations script --idempotent -o up.sql` → `docker exec -i postgres-main psql -U admin -d <db> < up.sql`) hoặc `dotnet ef database update`. **Preflight bắt buộc theo round (dọn TRƯỚC khi apply, không migration nào tự dọn):**
> - **S6 đợt 9 (DB10/DB15):** ⚠ CHECK constraints fail nếu data ngoài miền → trước apply: `UPDATE`/dọn row `campaign_criteria.weight`/`rubric_criteria.weight` ngoài **(0,1]** và `campaigns.pass_score_pct` ngoài **[0,100]**; bảng `subscriptions` phải **rỗng** (DROP TABLE). `rubric_anchors`→`rubric_levels.example_answers` backfill đã **L3 Postgres verify 0-loss** (throwaway PG) — an toàn. xmin = model-only, **0 DDL** (system column), không cần dọn.
> - **AI2 (RabbitMQ DLX/DLQ):** queue `scoring_pipeline_queue` LIVE khai `arguments=None` → **KHÔNG redeclare được** với arg `x-dead-letter-exchange` mới (PRECONDITION_FAILED 406, cả `aiworker` lẫn `interviewservice` fail khởi động). **Chọn 1:** (a) **recreate queue** — dừng consumer/publisher → `rabbitmqadmin delete queue name=scoring_pipeline_queue` (đảm bảo drain hết) → khởi động lại (2 bên tự redeclare với DLX arg); HOẶC (b) **RabbitMQ policy** không đụng queue arg: `rabbitmqctl set_policy scoring-dlx "^scoring_pipeline_queue$" '{"dead-letter-exchange":"scoring_pipeline_dlx","dead-letter-routing-key":"scoring_dead"}' --apply-to queues` (vẫn phải khai DLX `scoring_pipeline_dlx` + DLQ `scoring_pipeline_dead_queue` trước). Cách (b) **an toàn hơn** (không mất message đang chờ).

### Seed dữ liệu ban đầu (sau khi apply schema)

Schema rỗng ⇒ **thiếu 2 thứ** để luồng tiền chạy: **catalog gói credit** (`product_packages`) + **tài khoản Admin** (quản gói/duyệt postpaid). Rubric B2C đã bake sẵn trong Interview `InitialCreate`.

**1. Gói credit (`product_packages`) — bắt buộc trước khi mua credit:**
```bash
# DB rỗng → GET /payment/package trả [] → không mua được. Seed vài gói OneTime:
docker exec -i postgres-main psql -U admin -d isas_payment < scripts/seed-packages.sql
```
> `scripts/seed-packages.sql` idempotent (guard theo `name`), cột khớp migration `InitialCreate`. Sửa giá/số credit trong file trước khi chạy nếu cần.

**2. Tài khoản Admin — tạo THỦ CÔNG (không có API `register-as-admin`):**
AuthService dùng **ASP.NET Core Identity**; register chỉ cấp role `Candidate`/`Employer`. **KHÔNG** dựng user Identity bằng raw SQL (phải đúng `normalized_user_name`, `security_stamp`, `concurrency_stamp`, `password_hash`, + join `user_roles`/`roles` — sai 1 cột là login gãy âm thầm). Cách chắc chắn = **để Identity tự tạo row hợp lệ rồi nâng role bằng SQL**:
```bash
# (a) đăng ký user qua API (Identity tạo row + password + normalized fields đúng)
curl -X POST https://<gateway>/api/v1/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@isas.local","password":"<StrongPass>","fullName":"Platform Admin"}'

# (b) nâng lên Admin trong DB Auth (tạo role Admin nếu chưa có + gán user_roles)
docker exec -i postgres-main psql -U admin -d isas <<'SQL'
INSERT INTO roles (id, name, normalized_name, concurrency_stamp)
SELECT gen_random_uuid(), 'Admin', 'ADMIN', gen_random_uuid()
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE normalized_name = 'ADMIN');

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u CROSS JOIN roles r
WHERE u.normalized_email = 'ADMIN@ISAS.LOCAL' AND r.normalized_name = 'ADMIN'
  AND NOT EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = u.id AND ur.role_id = r.id);
SQL
```
> (c) **Login lại** sau khi nâng role → JWT mới mang `Admin` (role gắn vào token lúc login). Xong: `admin@` gọi được `POST/PUT/DELETE /payment/package…` (A5, `Roles="Admin"`) + `admin/invoices/close`.
>
> ⚠ `scripts/seed-test-users.sql` **chỉ `UPDATE password_hash`** cho row **đã tồn tại** (`hr@`/`admin@`) — nó **KHÔNG tạo** user. Phải register (bước a) hoặc dùng `seed-admin.sql` riêng (không có trong repo — chứa hash prod) trước.

---

## 5. MAC — AIService trong Docker

### 5a. Dockerfile — `src/services/Isas.AIService/Dockerfile`

```dockerfile
FROM python:3.12-slim
WORKDIR /app
# ffmpeg cho faster-whisper decode webm/opus
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY app ./app
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
```

> Dùng `python:3.12` — `ctranslate2`/`faster-whisper` chưa có wheel cho 3.14.

### 5b. Mac `compose.yaml`

```yaml
services:
  aiservice-api:                       # FastAPI: sinh câu hỏi + transcribe
    build: /path/to/Isas.AIService
    image: isas.aiservice:local
    container_name: aiservice-api
    command: uvicorn app.main:app --host 0.0.0.0 --port 8000
    environment:
      - GEMINI_API_KEY=${GEMINI_API_KEY}
      - GEMINI_MODEL=gemini-2.5-flash
    ports:
      - "8000:8000"                    # server gọi vào đây qua tailnet
    restart: unless-stopped

  aiservice-worker:                    # consumer chấm điểm (dùng chung image)
    image: isas.aiservice:local
    container_name: aiservice-worker
    command: python -m app.worker
    environment:
      - GEMINI_API_KEY=${GEMINI_API_KEY}
      - GEMINI_MODEL=gemini-2.5-flash
      - RABBITMQ_URL=amqp://${RABBITMQ_USER}:${RABBITMQ_PASS}@<SERVER_TS_IP>:5672/
      - QUEUE_NAME=scoring_pipeline_queue
      - S3_ENDPOINT=http://<SERVER_TS_IP>:8333
      - S3_ACCESS_KEY=${S3_ACCESS_KEY}
      - S3_SECRET_KEY=${S3_SECRET_KEY}
      - S3_BUCKET=isas-files
      - DOTNET_CALLBACK_BASE=http://<SERVER_TS_IP>:5246
      - INTERNAL_TOKEN=${INTERNAL_TOKEN}   # KHỚP server
    depends_on: [aiservice-api]
    restart: unless-stopped
```

### Bring-up Mac

```bash
cd ~/ai
docker compose up -d --build
docker compose logs -f aiservice-worker
```

---

## 6. Bảng cổng tham chiếu

| Service | Host | Cổng container | Publish | Ai truy cập |
|---|---|---|---|---|
| gateway | server | 8080 | 5050 | public (cloudflare) |
| interviewservice | server | 8080 | 5246 | Mac (callback) |
| seaweedfs S3 | server | 8333 | 8333 | Mac (tải audio) |
| rabbitmq | server | 5672 | 5672 | Mac (consume) |
| aiservice-api | Mac | 8000 | 8000 | server (sinh câu hỏi) |

---

## 7. Checklist / Gotcha

- [ ] **`.env` KHÔNG bọc dấu nháy khi chạy qua Docker** — `docker --env-file` / compose `env_file` truyền **nguyên cả `"..."`** vào biến môi trường (khác `python -m` chạy thẳng: pydantic/dotenv tự bỏ nháy). Vd `S3_ENDPOINT="http://ip:8333"` → boto3 báo `Invalid endpoint`. Viết **không nháy**: `S3_ENDPOINT=http://ip:8333`. *(Footgun thật, đã dính 2026-06-27.)*
- [ ] **Path-style S3** — khi endpoint là **IP**, boto3 **tự dùng path-style** → **không cần** cấu hình thêm (verify 2026-06-27: `list_objects`/download chạy với boto3 client mặc định trên SeaweedFS qua IP). *Chỉ* khi endpoint là **hostname/domain** mới phải ép path-style:
  ```python
  from botocore.config import Config
  s3_client = boto3.client('s3', endpoint_url=settings.s3_endpoint, ...,
      config=Config(s3={"addressing_style": "path"}))
  ```
- [ ] **`<MAC_TS_IP>` / `<SERVER_TS_IP>`** thay bằng IP Tailscale thật ở cả 2 phía.
- [ ] **Routing `/api/v1`** — frontend gọi `/api/v1/auth/...`, `/api/v1/interview/...`, `/api/v1/campaign/...`, `/api/v1/payment/...` (KHÔNG còn `/api/auth`). **`/api/v1/ai/*` đã gỡ (GEN-7)** — AI internal-only, FE không gọi trực tiếp.
- [ ] **Internal token** Interview ↔ Worker khớp, **Jwt** Auth ↔ Interview khớp.
- [ ] **CI không build AIService** — Mac build tay (`up -d --build`), không pull GHCR. Muốn pull thì thêm step CI buildx multi-arch (Mac là arm64).
- [ ] **RAM Mac**: api + worker đều load Whisper (2 model). Không dùng `/transcribe` thì bỏ `Transcriber()` trong `main.py` cho nhẹ.
- [ ] **Bucket `isas-files`** tự tạo bởi `BucketInitializer` của Interview — không cần tạo tay.
- [ ] Cổng tailnet (`5672/8333/5246/8000`) chặn public bằng firewall/Tailscale ACL.

---

## 8. Luồng end-to-end (kiểm tra nhanh)

1. FE → `gateway/api/v1/interview/practice/sessions` → Interview tạo session → gọi `AiService:BaseUrl` (Mac:8000) sinh câu hỏi.
2. FE upload answer → Interview lưu audio lên SeaweedFS → publish job lên RabbitMQ → answer = `Scoring`.
3. Worker (Mac) consume → tải audio (SeaweedFS) → Whisper transcribe → Gemini chấm → callback `interviewservice:5246/internal/answers/{id}/result`.
4. Interview lưu điểm → answer = `Scored`; lỗi vĩnh viễn → worker callback `/failed` → answer = `Failed`. Session đóng khi mọi answer xong.
