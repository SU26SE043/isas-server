# ISAS — Hướng dẫn Deploy (1 host)

**Toàn bộ stack chạy trên MỘT server Linux**: data layer (`postgres`, `redis`, `seaweedfs`,
`rabbitmq`, `qdrant`), 5 service .NET (`authservice`, `interviewservice`, `campaignservice`,
`paymentservice`, `gateway`) và **AIService** Python (`aiapi` + `aiworker`). CI/CD build **6**
image → push GHCR → SSH deploy.

> **Trước 2026-08-06 AIService chạy trên Mac** và gọi S3/RabbitMQ của server qua Tailscale.
> Đo được link đó bão hoà ~1 MB/s, mà audio phải đi vòng **server(S3) → Mac → OpenAI** ⇒ quét
> tải `/decide-next` knee ở 0,5 req/s (~60 người đồng thời). Đưa AIService về đây biến chặng S3
> thành loopback, chỉ còn upload ra OpenAI qua đường 6,5 MB/s.
>
> `SERVER_TS_IP` vẫn còn dùng cho `Internal__CallbackBase` của campaignservice — giữ dạng IP
> tailnet có chủ ý để đường lùi về Mac không phải đụng tới nó.

---

## 0. ⚠ Ba nơi mô tả cấu hình deploy — file compose trong repo KHÔNG phải cái đang chạy

| Nơi | Vai trò |
|---|---|
| `/home/duc2834/docker/main/docker-compose.yml` **trên server** | **Cái đang chạy thật** (compose project `main` — kiểm bằng label `com.docker.compose.project` của bất kỳ container nào). Nguồn sự thật về production. |
| [`deploy/compose.yaml`](deploy/compose.yaml) (repo) | Bản mirror trong version control. Khối cảnh báo đầu file liệt kê những chỗ **cố ý** còn khác server. |
| **`DEPLOYMENT.md` §4** (file này) | Tài liệu hướng dẫn — artifact **thứ ba**, dễ lệch nhất vì không ai chạy nó. |

Ba nơi đã trôi khỏi nhau theo **cả hai chiều** và từng để lại hậu quả thật: `ApiServices__<index>__…` làm **119/144 endpoint sai đường dẫn** trên Scalar (sửa ở PR #100), `Invitation__BaseUrl=${GATEWAY_PUBLIC_URL}` làm **magic-link mời B2B chết nhiều ngày** mà không ai báo (sửa ở `6507b2e`).

**⇒ Đổi cấu hình phải sửa CẢ BA nơi** cho tới khi hợp nhất được về một nguồn.

```bash
# kiểm lệch
ssh duc2834@100.64.204.33 "cat ~/docker/main/docker-compose.yml"   # so với deploy/compose.yaml
ssh duc2834@100.64.204.33 "grep -oE '^[A-Za-z0-9_]+' ~/docker/main/.env"   # chỉ TÊN biến, không giá trị
```

> **Đối chiếu gần nhất: 2026-08-02.** Danh sách chỗ **cố ý** còn khác server nằm ở đầu [`deploy/compose.yaml`](deploy/compose.yaml) — **đừng chép lại vào đây**, chép là đẻ ra artifact thứ tư.

---

## 1. Sơ đồ liên lạc

```
SERVER (Linux) — MỘT host, một compose network `isas-main-network`
┌───────────────────────────────────────────────────────────────────┐
│  postgres   redis   qdrant   seaweedfs:8333   rabbitmq:5672       │
│      ▲                            ▲                ▲              │
│      │                            │                │              │
│  ┌───┴──────────────┐    ┌────────┴────────────────┴───────────┐  │
│  │ authservice      │    │ aiapi   :8000  (expose, KHÔNG publish)│ │
│  │ interviewservice │◄──►│   sinh câu hỏi · /decide-next · TTS  │  │
│  │ campaignservice  │    │   phân tích CV · roadmap · embed     │  │
│  │ paymentservice   │◄───┤ aiworker  (không phục vụ HTTP)       │  │
│  │ gateway   :5050  │    │   kéo audio S3 · chấm Gemini         │  │
│  └──────────────────┘    │   sàng CV C14 · callback · usage F22 │  │
│         │                └──────────────────────────────────────┘  │
└─────────┼─────────────────────────────────────────────────────────┘
          ▼  public qua cloudflare tunnel (chỉ gateway)

Audio KHÔNG còn rời host: seaweedfs → aiapi là loopback. Chặng ra ngoài duy nhất
là aiapi/aiworker → OpenAI + Gemini.
```

**aiworker → phần còn lại** (nay đều là tên service trong cùng compose network): kéo job
`rabbitmq:5672`, tải audio `seaweedfs:8333`, callback chấm `isas.interviewservice:8080`, callback
sàng CV `campaignservice` (**C14**, lấy từ chính message), báo token/chi phí
`isas.paymentservice:8080` (**F22**), nạp prompt `isas.interviewservice:8080` (**F21**).
**interview/campaign → aiapi:** `AiService__BaseUrl=http://aiapi:8000` (sinh câu hỏi · `/decide-next`
· phân tích CV · roadmap · TTS · embed). **KHÔNG qua gateway** — GEN-7, AIService internal-only:
`aiapi` chỉ `expose`, không `ports`.
> ✅ Override `ai-cluster` thừa trên server đã được gỡ (đo 2026-08-06: gateway chỉ còn 4 cluster).
> **Đừng thêm lại** chỉ vì nay `aiapi` là container anh em.

---

## 2. Yêu cầu trước

- [ ] **Tailscale** cài trên **cả** Server và Mac, cùng tailnet. Lấy IP: `tailscale ip -4`.
  - `<SERVER_TS_IP>` = IP Tailscale của server → biến `.env` **`SERVER_TS_IP`**. Nay chỉ còn dùng cho `Internal__CallbackBase` của campaignservice (C14). Giữ dạng IP tailnet **có chủ ý**: nó tới được từ cả container trên server lẫn từ Mac, nên đường lùi về Mac không phải đụng tới nó.
  - *(`MAC_TS_IP` đã bỏ — AIService không còn ở Mac.)*
- [ ] **Docker + Docker Compose** trên cả 2 máy.
- [ ] Firewall/Tailscale ACL: cổng `5672`, `8333`, `5246`, `5247` **chỉ** cho phép tailnet — không lộ public. `aiapi:8000` KHÔNG publish ra host (chỉ `expose`), nên không cần luật riêng.
  - ⚠ Riêng **`5271`** (payment) vừa nhận callback usage từ Mac **vừa phải cho webhook PayOS gọi vào** ⇒ cần public URL/tunnel, không chặn được về tailnet-only.

---

## 3. Secret phải KHỚP nhau

| Secret | Dùng ở | Quy tắc |
|---|---|---|
| `Jwt__Key` / `Jwt__Issuer` / `Jwt__Audience` | authservice ↔ **interview · campaign · payment** | **giống hệt** (3 service kia validate offline token do Auth phát — GEN-3, không gọi Auth lúc chạy) |
| `Internal__Token` ↔ `INTERNAL_TOKEN` | **auth · interview · campaign · payment** ↔ **aiapi · aiworker** | **giống hệt** — một token duy nhất cho MỌI callback máy-máy `/internal/*`: chấm điểm (worker→interview), sàng CV C14 (worker→campaign), báo usage F22 (api+worker→payment), provision-candidate D2 (campaign→auth). Lệch 1 ký tự = 401 âm thầm ở đúng nhánh đó *(đã dính 2026-07-15)*. |
| SeaweedFS access/secret | interviewservice · campaignservice ↔ aiworker ↔ `seaweed-s3.json` | cùng giá trị (S3 dùng chung) |

> Giá trị thật để trong file `.env` cạnh compose trên server / Mac — **không** ghi vào file md này.

---

## 4. SERVER — `~/docker/main/docker-compose.yml`

> Bản dưới đây là **mirror của [`deploy/compose.yaml`](deploy/compose.yaml)** (bản trong version control, mặc định AN TOÀN). Cái **đang chạy** là file trên server — xem §0. Chỗ cố ý còn khác server: đọc khối cảnh báo đầu `deploy/compose.yaml`.

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

  # RAG grounding (D27) — kho vector chunk tri thức. Chỉ InterviewService gọi (gRPC 6334). Volume bền.
  qdrant:
    image: qdrant/qdrant:latest
    container_name: qdrant-main
    restart: unless-stopped
    # ⚠ CỐ Ý KHÔNG publish 6333/6334 ra host — khớp production. InterviewService gọi qdrant qua
    # network nội bộ (`Qdrant__Url=http://qdrant:6334`), không cần cổng ngoài; publish ra là mở
    # dashboard REST 6333 cho mọi thứ tới được host. Cần xem dashboard thì `docker compose port`
    # hoặc ssh tunnel, đừng mở cố định.
    volumes:
      - qdrant_main_data:/qdrant/storage
    networks: [isas-main-network]

  # ===== APP SERVICES =====
  isas.authservice:
    image: ghcr.io/su26se043/isas.authservice:main
    container_name: authservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      # Auth cũng phục vụ callback máy-máy `/internal/auth/provision-candidate` (D2 — join magic-link
      # B2B tạo tài khoản Candidate nhẹ). Thiếu token này → Campaign gọi sang bị từ chối, join B2B hỏng.
      - Internal__Token=${INTERNAL_TOKEN}
      # DB28 — job dọn refresh_token hết hạn. Giữ TẮT như production: xoá dữ liệu auth là không đảo
      # ngược được. Bật sau khi quan sát 1 chu kỳ — quyết định ops, không phải mặc định.
      - RefreshTokenRetention__Enabled=false
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
      # Đăng nhập Google: callback 302 về FE kèm MÃ DÙNG-MỘT-LẦN (?code=…), KHÔNG kèm token; FE đổi
      # mã lấy phiên qua POST /auth/google/exchange. Cả 2 URL lấy từ CONFIG SERVER (không nhận đích
      # từ client — nếu không là open-redirect làm rò phiên).
      # ⚠ TÊN BIẾN theo .env ĐANG CHẠY trên server: `FRONTEND_PUBLIC_URL` / `GOOGLE_PUBLIC_BASE_URL`
      # (tài liệu này trước đây ghi tên cũ `FRONTEND_BASE_URL` / `GATEWAY_PUBLIC_BASE_URL` — đã sửa 2026-08-02).
      # `GOOGLE_PUBLIC_BASE_URL` phải KÈM `/api/v1` (gateway strip tiền tố) và khớp "Authorized redirect
      # URI" khai trên Google Cloud Console: {GOOGLE_PUBLIC_BASE_URL}/auth/signin-google
      - Frontend__BaseUrl=${FRONTEND_PUBLIC_URL}
      - Gateway__PublicBaseUrl=${GOOGLE_PUBLIC_BASE_URL}
      # ⓘ TUỲ CHỌN, hiện KHÔNG compose nào đặt (server lẫn repo) ⇒ đang lấy mặc định `appsettings.json`
      #   = 60s. Code vẫn đọc khoá này (`GoogleAuthCodeStore.cs:58`), giá trị ngoài [5, 600] bị kẹp.
      #   Chỉ thêm dòng dưới khi thật sự cần đổi TTL:
      # - Authentication__Google__OneTimeCodeTtlSeconds=${GOOGLE_ONETIME_CODE_TTL_SECONDS:-60}
      # ⚠ Mã giữ trong BỘ NHỚ tiến trình AuthService: chỉ đổi được ở ĐÚNG instance đã phát, và
      # restart/deploy làm mất mã đang bay (user bấm đăng nhập Google lại là xong). Chạy NHIỀU
      # instance AuthService ⇒ phải bật sticky session hoặc chuyển kho mã sang Redis/DB.
    expose: ["8080"]
    depends_on: [postgres, redis]
    networks: [isas-main-network]
    restart: unless-stopped

  isas.interviewservice:
    image: ghcr.io/su26se043/isas.interviewservice:main
    container_name: interviewservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      # DB28 — dọn outbox_messages đã publish. Giữ TẮT như production (cùng lý do RefreshTokenRetention).
      - Outbox__PurgeEnabled=false
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=isas_interview;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Jwt__Key=${JWT_KEY}                         # KHỚP authservice
      - Jwt__Issuer=http://isas.authservice:8080
      - Jwt__Audience=http://isas.authservice:8080
      - Internal__Token=${INTERNAL_TOKEN}           # KHỚP aiworker
      - SeaweedFS__ServiceURL=http://seaweedfs:8333
      - SeaweedFS__AccessKey=${S3_ACCESS_KEY}
      - SeaweedFS__SecretKey=${S3_SECRET_KEY}
      - SeaweedFS__BucketName=isas-files
      - SeaweedFS__ForcePathStyle=true
      - SeaweedFS__UseHttp=true
      - RabbitMQ__HostName=rabbitmq
      - RabbitMQ__UserName=${RABBITMQ_USER}
      - RabbitMQ__Password=${RABBITMQ_PASS}
      - AiService__BaseUrl=http://aiapi:8000          # cùng compose network (trước 2026-08-06: Mac qua Tailscale)
      # RAG grounding (D27) — kho vector Qdrant + ingest Context7 + toggle bật retrieval lúc SINH.
      # Mặc định TẮT: bật khi đã nạp corpus, nếu không mọi request đều "ungrounded" mà vẫn tốn 1 vòng embed.
      - Qdrant__Url=http://qdrant:6334
      - Context7__ApiKey=${CONTEXT7_API_KEY}
      - Grounding__Enabled=${GROUNDING_ENABLED:-false}
      # ===== Phỏng vấn THÍCH ỨNG (INT-17 / INT-17b) =====
      # Trước đây các khoá này CHỈ tồn tại trong compose sửa TAY trên server ⇒ hình dạng buổi phỏng vấn
      # chạy thật chỉ đọc được bằng `docker inspect`, và thêm một khoá mới vào appsettings là âm thầm
      # đổi hành vi production. Nay tham số hoá qua .env, mặc định AN TOÀN.
      #
      # ⚠ KILL-SWITCH = `Adaptive__MaxDeepPerQuestion` (`ADAPTIVE_MAX_DEEP_PER_QUESTION`):
      #     >0 → chế độ CHUỖI (mỗi câu gốc đào sâu tối đa ngần đó tầng, xen kẽ ngay sau nó);
      #      0 → chế độ frontier trước INT-17b (lúc đó `MaxFollowUps` mới có hiệu lực — ở chế độ chuỗi
      #          code tự ép nó về 0, nên ĐỪNG đặt `MaxFollowUps=0` để "tắt", làm vậy là bỏ trần buổi).
      #   Tắt hẳn phỏng vấn thích ứng (về luồng batch tĩnh cũ): `Adaptive__Enabled=false`.
      # ⚠ `SeedCount` là TRẦN TRÊN của số câu gốc; số thực tế = ceil(trần buổi / (1 + độ sâu)) vì ứng viên
      #   chọn "số câu" là chọn TỔNG số câu của buổi (F2b). Trần 20 → 5 gốc · 10 → 3 · 5 → 2.
      - Adaptive__Enabled=${ADAPTIVE_ENABLED:-false}
      - Adaptive__SeedCount=${ADAPTIVE_SEED_COUNT:-5}
      - Adaptive__MaxQuestions=${ADAPTIVE_MAX_QUESTIONS:-20}
      - Adaptive__MaxFollowUps=${ADAPTIVE_MAX_FOLLOW_UPS:-3}
      - Adaptive__MaxDeepPerQuestion=${ADAPTIVE_MAX_DEEP_PER_QUESTION:-3}
      - Adaptive__MaxFailuresPerSession=${ADAPTIVE_MAX_FAILURES_PER_SESSION:-3}
    ports:
      - "5246:8080"     # publish để Mac gọi callback /internal/... qua tailnet
    depends_on: [postgres, seaweedfs, rabbitmq, qdrant]
    networks: [isas-main-network]
    restart: unless-stopped

  isas.campaignservice:
    image: ghcr.io/su26se043/isas.campaignservice:main
    container_name: campaignservice-main
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      # DB2b — dọn outbox_messages (email mời) đã publish. Giữ TẮT như production.
      - Outbox__PurgeEnabled=false
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=isas_campaign;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Jwt__Key=${JWT_KEY}
      - Jwt__Issuer=http://isas.authservice:8080
      - Jwt__Audience=http://isas.authservice:8080
      - AiService__BaseUrl=http://aiapi:8000
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
      # D28 — B2B tier gate giữ TẮT tới khi verify staging. Khi bật, client này phải gọi THẲNG Payment
      # kèm internal token, KHÔNG qua gateway. ⚠ Chưa có trên server.
      - Payment__BaseUrl=http://isas.paymentservice:8080
      - Tiering__Enabled=${TIERING_ENABLED:-false}
      # D2 — join magic-link: Campaign gọi Auth `/internal/auth/provision-candidate` (tạo/lấy tài khoản
      # Candidate theo email) rồi Interview `/internal/sessions/campaign` (create-or-get session lúc
      # Start). THIẾU 2 base URL này → join/start B2B 500 (bắt ở đợt hardening 2026-07-15).
      - Auth__BaseUrl=http://isas.authservice:8080
      - Interview__BaseUrl=http://isas.interviewservice:8080
      - Internal__Token=${INTERNAL_TOKEN}
      # C14 — worker sàng CV B2B chạy TRÊN MAC, nên callback phải là địa chỉ tới được qua tailnet, không
      # phải `http://localhost:8080` (mặc định trong code — `CvScreeningService.cs:58`) và cũng KHÔNG
      # qua gateway (GEN-1). ⇒ campaignservice PHẢI publish cổng 5247 (bên dưới) để Mac gọi ngược vào.
      - Internal__CallbackBase=http://${SERVER_TS_IP}:5247
      # SMTP — InvitationEmailConsumer gửi email mời (magic-link) khi tiêu thụ campaign_invitation_email_queue.
      - EmailSettings__Host=${SMTP_HOST}
      - EmailSettings__Port=${SMTP_PORT}
      - EmailSettings__Username=${SMTP_USER}
      - EmailSettings__Password=${SMTP_PASS}
      - EmailSettings__From=${SMTP_FROM}
      # Magic-link ứng viên = {baseUrl}/invite/{token} — PHẢI là origin của FRONTEND.
      # 🔴 Trước đây chỗ này ghi `${GATEWAY_PUBLIC_URL}` + path `/invitations/`: sai HAI lớp (gateway chỉ
      # phục vụ `/api/v1/...`, còn `/invitations/:token` là route API trả JSON — route FE là `invite/:token`)
      # ⇒ link trong email trả 404 body RỖNG = trang trắng, nhìn y như đang tải, sống nhiều ngày không ai
      # báo. Sửa ở `6507b2e` (+ gỡ hẳn fallback về `Gateway:Url`, vì fallback làm cấu hình TRÔNG như đã đặt).
      - Invitation__BaseUrl=${FRONTEND_PUBLIC_URL}
    ports:
      - "5247:8080"     # publish để worker sàng CV trên Mac gọi callback /internal/... qua tailnet (C14)
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
      # DB18 — OrphanReservationReconciler gọi Interview /internal/sessions/exists để dọn chỗ giữ credit
      # mồ côi. Để TRỐNG → reconciler safe-skip mỗi vòng (không release mù), credit treo không ai dọn.
      - Interview__BaseUrl=http://isas.interviewservice:8080
      # BK24 — `InvoiceOverdueReconciler` đóng dấu hoá đơn postpaid Issued→Overdue quá hạn. Đó là cái
      # PHANH của BK17 (org có hoá đơn Overdue thì reserve → 402): không bật thì postpaid là "trả sau"
      # KHÔNG có phanh. Bật thật trên server 2026-07-23.
      - InvoiceOverdue__Enabled=true
      - InvoiceOverdue__GraceHours=24
      - InvoiceOverdue__ScanIntervalSeconds=600
      # D28 — giữ tường minh cả hai lá chắn trong lúc rollout additive. ⚠ Chưa có trên server.
      - Tiering__Enabled=${TIERING_ENABLED:-false}
      - Tiering__AllowUnlimitedPlans=${TIERING_ALLOW_UNLIMITED_PLANS:-false}
      # F7 — suất dùng thử tặng lúc tạo ví User. Có default :-3 vì chuỗi RỖNG không parse được thành
      # int (env thiếu → options binder ném lúc khởi động). Đặt 0 để tắt hẳn.
      - Billing__FreeTrialCredits=${FREE_TRIAL_CREDITS:-3}
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
      # GEN-7 (2026-07-13): ai-cluster + /api/v1/ai route đã GỠ khỏi gateway (AI internal-only qua
      # AiService:BaseUrl). ⚠ Server hiện VẪN còn dòng override `ai-cluster` — nên xoá bên đó, đừng
      # thêm lại vào đây. Lưu ý `ai-route` + khối chặn `/internal/*` chỉ ĐƯỢC BẬT khi môi trường ≠
      # Development — mà production đang chạy Development (OPS6) ⇒ /api/v1/ai hiện vẫn đi qua gateway.
      #
      # 🔴 ApiServices phải khai theo TÊN (`ApiServices__<tên>__OpenApiUrl`), KHÔNG theo index.
      # Dạng cũ `ApiServices__0__…` là bug đã sửa ở PR #100: appsettings khai 5 entry còn compose khai
      # 4 URL ⇒ mỗi service ghép nhầm `Prefix` của service khác (119/144 op sai đường dẫn trên Scalar),
      # Payment không có entry nên đổ hết path ra root. Với bản keyed hiện tại, `__0__` còn tệ hơn: nó
      # tạo entry tên "0" KHÔNG có Prefix (bị bỏ qua + log Error) trong khi 4 service thật giữ nguyên
      # URL localhost:517x của appsettings ⇒ gộp OpenAPI hỏng SẠCH.
      # Tên hợp lệ: auth · interview · campaign · payment.
      - ApiServices__auth__OpenApiUrl=http://isas.authservice:8080/openapi/v1.json
      - ApiServices__interview__OpenApiUrl=http://isas.interviewservice:8080/openapi/v1.json
      - ApiServices__campaign__OpenApiUrl=http://isas.campaignservice:8080/openapi/v1.json
      - ApiServices__payment__OpenApiUrl=http://isas.paymentservice:8080/openapi/v1.json
      - Gateway__Url=${GATEWAY_PUBLIC_URL}
      # ⚠ Mỗi `Cors__AllowedOrigins__N` GHI ĐÈ phần tử thứ N của mảng trong appsettings.json (5 phần tử)
      # — KHÔNG phải nối thêm. Vì thế phải khai LẠI tường minh origin FE production và localhost:4200.
      # ⚠ Server đang khai TRÙNG index 4 hai lần (`${GATEWAY_PUBLIC_URL}` rồi `http://localhost:4200`)
      # → dòng sau thắng, `GATEWAY_PUBLIC_URL` rơi khỏi CORS. Bố cục dưới đây giữ đủ cả hai.
      - Cors__AllowedOrigins__0=http://localhost:3000
      - Cors__AllowedOrigins__1=http://localhost:5173
      - Cors__AllowedOrigins__2=http://localhost:5174
      - Cors__AllowedOrigins__3=https://isas-web-client.vercel.app
      - Cors__AllowedOrigins__4=${GATEWAY_PUBLIC_URL}
      - Cors__AllowedOrigins__5=https://sep-490-angular.vercel.app
      - Cors__AllowedOrigins__6=http://localhost:4200
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
  qdrant_main_data:   # RAG grounding (D27)
```

### Server `.env` (cạnh compose, `chmod 600`)

> **Nguồn sự thật cho từng khoá = [`.env.example`](.env.example)** (có giải thích đầy đủ vì sao + hậu quả khi thiếu). Bảng dưới chỉ liệt kê tên để tra nhanh.

```env
# ===== Hạ tầng =====
POSTGRES_USER=admin
POSTGRES_PASSWORD=...
JWT_KEY=...                    # PHẢI giống hệt ở auth · interview · campaign · payment
INTERNAL_TOKEN=...             # PHẢI giống hệt ở 4 service .NET + aiapi + aiworker
S3_ACCESS_KEY=admin
S3_SECRET_KEY=...
RABBITMQ_USER=guest
RABBITMQ_PASS=guest

# ===== SMTP (Auth gửi OTP/reset · Campaign gửi email mời B2B) =====
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USER=...
SMTP_PASS=...
SMTP_FROM=...

# ===== Google OAuth + origin công khai =====
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
# ⚠ TÊN BIẾN đúng (khớp .env đang chạy). Trước 2026-08-02 tài liệu này ghi nhầm `FRONTEND_BASE_URL`
#   và `GATEWAY_PUBLIC_BASE_URL` — hai tên đó KHÔNG tồn tại ở đâu cả.
# Origin FE. CÙNG biến này còn là `Invitation__BaseUrl` của campaign (magic-link = {origin}/invite/{token}).
FRONTEND_PUBLIC_URL=https://<your-frontend>
# Origin gateway KÈM /api/v1 (gateway strip tiền tố). Redirect URI khai trên Google Cloud Console:
#   {GOOGLE_PUBLIC_BASE_URL}/auth/signin-google
GOOGLE_PUBLIC_BASE_URL=https://<your-tunnel>.trycloudflare.com/api/v1
# Origin gateway KHÔNG kèm /api/v1 — dùng cho CORS + Scalar.
GATEWAY_PUBLIC_URL=https://<your-tunnel>.trycloudflare.com

# ===== Mạng Tailscale (2 host) =====
SERVER_TS_IP=100.64.204.33     # chiều ngược: callback C14 (campaign:5247), usage F22, RabbitMQ, S3

# ===== PayOS (mua credit + webhook) =====
PAYOS_CLIENT_ID=...
PAYOS_API_KEY=...
PAYOS_CHECKSUM_KEY=...
# BF3 — bắt buộc: thiếu → POST /order 502 (PayOS reject "return_url null").
PAYOS_RETURN_URL=https://<your-frontend-or-tunnel>/payment/success
PAYOS_CANCEL_URL=https://<your-frontend-or-tunnel>/payment/cancel

# ===== Feature flag (mặc định AN TOÀN — bật tường minh sau khi verify) =====
FREE_TRIAL_CREDITS=3           # F7 — credit tặng khi TẠO ví User. Bỏ trống = 3, đặt 0 = tắt.
TIERING_ENABLED=false          # D28 — gói phân tầng
TIERING_ALLOW_UNLIMITED_PLANS=false
CONTEXT7_API_KEY=...           # D27 — ingest corpus grounding
GROUNDING_ENABLED=false        # D27 — bật retrieval lúc SINH; chỉ bật sau khi đã nạp corpus
# INT-17b — KILL-SWITCH là ADAPTIVE_MAX_DEEP_PER_QUESTION (0 = về chế độ frontier trước INT-17b).
ADAPTIVE_ENABLED=false
ADAPTIVE_SEED_COUNT=5
ADAPTIVE_MAX_QUESTIONS=20
ADAPTIVE_MAX_FOLLOW_UPS=3
ADAPTIVE_MAX_DEEP_PER_QUESTION=3
ADAPTIVE_MAX_FAILURES_PER_SESSION=3

# ⓘ Tuỳ chọn, hiện KHÔNG đặt ở đâu → lấy mặc định appsettings (60s):
# GOOGLE_ONETIME_CODE_TTL_SECONDS=60
```

> ✅ **Đã hợp nhất 2026-08-02**: `.env` server nay có đủ 9 khoá từng thiếu (`SERVER_TS_IP` · 2× `TIERING_*` · 6× `ADAPTIVE_*`), và `~/docker/main/docker-compose.yml` đã được thay bằng bản khớp `deploy/compose.yaml`. Xác nhận bằng `docker compose config` (0 cảnh báo biến chưa set) rồi `up -d`; `docker inspect interviewservice-main` cho thấy đủ 6 khoá `Adaptive__*`.
> Chỉ còn `GOOGLE_ONETIME_CODE_TTL_SECONDS` là tuỳ chọn không đặt ở đâu (mặc định 60s).
> 🔴 **Bẫy vẫn còn giá trị cho lần deploy sau:** dùng `deploy/compose.yaml` trên một máy mà `.env` thiếu `ADAPTIVE_*` ⇒ `ADAPTIVE_ENABLED` rơi về `false` = **TẮT phỏng vấn thích ứng trong im lặng**; thiếu `SERVER_TS_IP` ⇒ `Internal__CallbackBase=http://:5247` = **callback C14 chết**. Cả hai đều không có lỗi nào báo — chỉ tính năng ngừng chạy.
> ⓘ Trước lần hợp nhất này production chạy **1 câu gốc × 3 tầng** (`SeedCount=1`, `MaxQuestions=6` ghi cứng trong compose server, 2 khoá còn lại lấy mặc định appsettings). Nay là **5 × 3** đúng thiết kế INT-17b.

### Server `seaweed-s3.json` (cạnh compose) — identities cho S3 auth
Seaweed bật auth bằng file này (`-s3.config` ở trên). `accessKey`/`secretKey` phải **khớp** `S3_ACCESS_KEY`/`S3_SECRET_KEY` trong `.env` **và** `aiworker`.
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
> docker exec -i postgres-main psql -v ON_ERROR_STOP=1 -U admin -d <db> < init_<db>.sql
> ```
> ⚠ **`-v ON_ERROR_STOP=1` là BẮT BUỘC, không phải tuỳ chọn.** `psql` trần chạy tiếp sau lỗi
> và **thoát 0** ⇒ script chết giữa chừng nhưng bạn thấy "thành công", để lại DB **migrate dở**
> (một phần bảng/cột đã đổi, phần còn lại chưa) — trạng thái tệ hơn cả không chạy gì.
> Đổi/reset schema → **drop & tạo lại DB** trước khi apply (squash chỉ sạch trên DB rỗng).

> **S6 hardening rounds — apply migration TĂNG DẦN (DB đã có data, KHÔNG drop):** dùng **idempotent script** (`dotnet ef migrations script --idempotent -o up.sql` → `docker exec -i postgres-main psql -v ON_ERROR_STOP=1 -U admin -d <db> < up.sql`) hoặc `dotnet ef database update`. ⚠ **`-v ON_ERROR_STOP=1` bắt buộc** — xem cảnh báo ở khối trên. **Preflight bắt buộc theo round (dọn TRƯỚC khi apply, không migration nào tự dọn):**
> - **S6 đợt 9 (DB10/DB15):** ⚠ CHECK constraints fail nếu data ngoài miền → trước apply: `UPDATE`/dọn row `campaign_criteria.weight`/`rubric_criteria.weight` ngoài **(0,1]** và `campaigns.pass_score_pct` ngoài **[0,100]**; bảng `subscriptions` phải **rỗng** (DROP TABLE). `rubric_anchors`→`rubric_levels.example_answers` backfill đã **L3 Postgres verify 0-loss** (throwaway PG) — an toàn. xmin = model-only, **0 DDL** (system column), không cần dọn.
> - **AI2 (RabbitMQ DLX/DLQ):** queue `scoring_pipeline_queue` LIVE khai `arguments=None` → **KHÔNG redeclare được** với arg `x-dead-letter-exchange` mới (PRECONDITION_FAILED 406, cả `aiworker` lẫn `interviewservice` fail khởi động). **Chọn 1:** (a) **recreate queue** — dừng consumer/publisher → `rabbitmqadmin delete queue name=scoring_pipeline_queue` (đảm bảo drain hết) → khởi động lại (2 bên tự redeclare với DLX arg); HOẶC (b) **RabbitMQ policy** không đụng queue arg: `rabbitmqctl set_policy scoring-dlx "^scoring_pipeline_queue$" '{"dead-letter-exchange":"scoring_pipeline_dlx","dead-letter-routing-key":"scoring_dead"}' --apply-to queues` (vẫn phải khai DLX `scoring_pipeline_dlx` + DLQ `scoring_pipeline_dead_queue` trước). Cách (b) **an toàn hơn** (không mất message đang chờ).
> - **Campaign ranking DLX (PR #138 follow-up):** deploy CampaignService trước để nó khai exchange `campaign.ranking.dlx` và queue `campaign.ranking.dead`, rồi gắn policy vào **queue hiện hữu** (không thêm queue arguments, tránh 406): `rabbitmqctl set_policy campaign-ranking-dlx "^campaign\.ranking$" '{"dead-letter-exchange":"campaign.ranking.dlx","dead-letter-routing-key":"campaign.ranking"}' --apply-to queues`. Kiểm tra bằng `rabbitmqctl list_queues name messages` sau một event lỗi lần 2; message phải ở `campaign.ranking.dead`, không biến mất.

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

## 5. AIService (Python) — nay nằm trong compose server

**Không còn mục riêng.** `aiapi` + `aiworker` khai ngay trong `deploy/compose.yaml` (khối
`x-aiservice` + 2 service), dùng chung anchor env để hai container **không thể lệch nhau** — đó
là lỗi đã cháy thật: 2026-08-05 chúng chạy hai image khác nhau 3 ngày, và trước đó
`USAGE_SINK_BASE`/`PROMPT_REGISTRY_BASE` vắng ở một bên khiến F21 + F22 tắt câm nhiều ngày.

Dockerfile: `src/services/Isas.AIService/Dockerfile` — nguồn sự thật DUY NHẤT. *(Mục 5a cũ chép
lại nó và đã trôi: bản chép thiếu `libgl1 libglib2.0-0` mà bản thật đã có từ lâu. Không chép nữa.)*

Ba thứ dễ quên khi dựng lại từ đầu:

| | |
|---|---|
| **2 volume model** | `ai_hf_cache` (faster-whisper) + `ai_insightface` (buffalo_l). Thiếu = tải lại ~800 MB **mỗi lần recreate**, mà từ nay MỌI push lên `main` đều recreate. |
| **Nạp trước volume** | Lần đầu volume rỗng, `aiapi` chưa bind `:8000` cho tới khi tải xong. Chạy trước một container tạm cùng volume để kéo model, rồi mới `up -d` — khác nhau giữa cửa sổ 20 giây và 5 phút. |
| **`GEMINI_API_KEY`** | Env **duy nhất** không có default: thiếu là chết lúc `import app.config`, không phải lỗi runtime. |

Bring-up: `docker compose up -d aiapi aiworker` (image từ GHCR, CI build — **không** build tay nữa).


## 6. Bảng cổng tham chiếu

Từ 2026-08-06 mọi service ở **cùng một host**; các `publish` dưới đây phần lớn là **di sản của
thời AIService còn ở Mac** — nay chúng chỉ còn dùng để gỡ rối từ tailnet, không phải đường chạy.

| Service | Cổng container | Publish | Ai truy cập |
|---|---|---|---|
| gateway | 8080 | 5050 | public (cloudflare) |
| interviewservice | 8080 | 5246 | *(di sản)* — nội bộ gọi `isas.interviewservice:8080` |
| **campaignservice** | 8080 | **5247** | `Internal__CallbackBase` (**C14**) vẫn dùng IP tailnet |
| **paymentservice** | 8080 | **5271** | **webhook PayOS** (public/tunnel) |
| seaweedfs S3 | 8333 | 8333 | *(di sản)* — `aiapi`/`aiworker` gọi `seaweedfs:8333` |
| rabbitmq | 5672 | 5672 | *(di sản)* — `aiworker` gọi `rabbitmq:5672` |
| **qdrant** | 6334 (gRPC) · 6333 (REST) | *(không publish)* | chỉ interviewservice, network nội bộ |
| authservice | 8080 | *(expose)* | chỉ nội bộ network |
| **aiapi** | 8000 | **(expose, KHÔNG publish)** | chỉ interview + campaign — **GEN-7 internal-only** |
| **aiworker** | — | — | không phục vụ HTTP, chỉ consume RabbitMQ |

---

## 7. Checklist / Gotcha

- [ ] **`.env` KHÔNG bọc dấu nháy khi chạy qua Docker** — `docker --env-file` / compose `env_file` truyền **nguyên cả `"..."`** vào biến môi trường (khác `python -m` chạy thẳng: pydantic/dotenv tự bỏ nháy). Vd `S3_ENDPOINT="http://ip:8333"` → boto3 báo `Invalid endpoint`. Viết **không nháy**: `S3_ENDPOINT=http://ip:8333`. *(Footgun thật, đã dính 2026-06-27.)*
- [ ] **Path-style S3** — khi endpoint là **IP**, boto3 **tự dùng path-style** → **không cần** cấu hình thêm (verify 2026-06-27: `list_objects`/download chạy với boto3 client mặc định trên SeaweedFS qua IP). *Chỉ* khi endpoint là **hostname/domain** mới phải ép path-style:
  ```python
  from botocore.config import Config
  s3_client = boto3.client('s3', endpoint_url=settings.s3_endpoint, ...,
      config=Config(s3={"addressing_style": "path"}))
  ```
- [ ] **Đổi cấu hình = sửa CẢ BA nơi** (server compose · `deploy/compose.yaml` · file này) — xem §0. Kiểm lệch trước mỗi lần deploy, đừng tin trí nhớ.
- [ ] **`<SERVER_TS_IP>`** thay bằng IP Tailscale thật (biến `.env`: `SERVER_TS_IP`) — nay chỉ còn dùng cho `Internal__CallbackBase` của campaignservice.
- [ ] **C14 — bật consumer sàng CV đúng THỨ TỰ**: `CV_SCREENING_ENABLED` (Mac worker) mặc định **`false`**. Bật trước khi xả queue tồn ⇒ chấm lại toàn bộ bản nhân đôi mà `StuckScreeningRepublisher` đã đẩy (đo 2026-08-02: **713 message cho đúng 8 ứng viên**). Trình tự: deploy code (tắt) → **xả `cv_screening_queue`** → `CV_SCREENING_ENABLED=true` → đợi ≤15' xem ứng viên rời `Analyzing`. Xả queue an toàn: 8 ứng viên vẫn ở `Analyzing` nên republisher tự đẩy lại đúng 8 job.
- [ ] **C14 — callback về `campaignservice:5247`**: cần `Internal__CallbackBase=http://${SERVER_TS_IP}:5247` **và** campaignservice publish cổng 5247. Thiếu 1 trong 2 → worker callback vào `http://localhost:8080` (mặc định trong code) = kết quả sàng CV không bao giờ về.
- [ ] **Routing `/api/v1`** — frontend gọi `/api/v1/auth/...`, `/api/v1/interview/...`, `/api/v1/campaign/...`, `/api/v1/payment/...` (KHÔNG còn `/api/auth`). **`/api/v1/ai/*` đã gỡ (GEN-7)** — AI internal-only, FE không gọi trực tiếp.
- [ ] **Internal token** Interview ↔ Worker khớp, **Jwt** Auth ↔ Interview khớp.
- [x] **CI build AIService** → `ghcr.io/<owner>/isas.aiservice:main` (từ 2026-08-06, `ci.yml` build **6** image). Trước đó Mac build tay, và hệ quả đã cháy: `aiapi` từng chạy image **cũ hơn `aiworker` 3 ngày** dù cùng tag `:local`. Verify bằng label: `docker inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' aiapi-main aiworker-main` — phải GIỐNG NHAU và bằng SHA của `main`.
- [x] **RAM**: trước đây api + worker đều nạp Whisper lúc import (đo `ru_maxrss`: +778 MB mỗi tiến trình, +358 MB nữa cho InsightFace ở api). ⚠ Lời khuyên cũ *"bỏ `Transcriber()` trong main.py cho nhẹ"* nay **SAI** — `/decide-next` cần transcriber. Câu trả lời đúng là **nạp lười** (đã làm 2026-08-06): model dựng ở lần dùng đầu, lúc rỗi chỉ còn ~150-250 MB.
- [ ] **Bucket `isas-files`** tự tạo bởi `BucketInitializer` của Interview — không cần tạo tay.
- [ ] Cổng tailnet (`5672/8333/5246/8000`) chặn public bằng firewall/Tailscale ACL.

---

## 8. Luồng end-to-end (kiểm tra nhanh)

1. FE → `gateway/api/v1/interview/practice/sessions` → Interview tạo session → gọi `AiService:BaseUrl` (Mac:8000) sinh câu hỏi.
2. FE upload answer → Interview lưu audio lên SeaweedFS → publish job lên RabbitMQ → answer = `Scoring`.
3. Worker (Mac) consume → tải audio (SeaweedFS) → Whisper transcribe → Gemini chấm → callback `interviewservice:5246/internal/answers/{id}/result`.
4. Interview lưu điểm → answer = `Scored`; lỗi vĩnh viễn → worker callback `/failed` → answer = `Failed`. Session đóng khi mọi answer xong.

---

## 9. Rollback (khi deploy hỏng)

> **Đọc hết mục này TRƯỚC khi lùi qua một mốc có migration.** Lùi code và lùi schema là
> hai bài toán khác nhau, và chỉ một trong hai làm được.

### 9.1 Lùi CODE — dùng tag bất biến `:main-<sha>`

`:main` là tag **di động**: mỗi lần push lên `main` nó trỏ sang image mới, nên bản vừa hỏng
đã chiếm mất cái tên đó. Repo cũng **không có tag git nào** để lần ra bản tốt. Vì vậy CI đẩy
**tag đôi**: `:main` (di động) **+** `:main-<sha>` (**bất biến**, gắn cứng vào một commit).

> 🔴 **GIỚI HẠN — đọc trước khi trông cậy vào mục này.** Tag `:main-<sha>` chỉ tồn tại cho
> image build **SAU** khi CI bắt đầu đẩy tag đôi. Mọi image build **trước** mốc đó chỉ từng
> mang `:main`, và cái tên đó nay đã bị bản mới chiếm ⇒ **không lùi về trước mốc đó được**.
> Kiểm bản nào thật sự lùi được — đừng giả định:
> ```bash
> docker image inspect ghcr.io/su26se043/isas.gateway:main-<sha> >/dev/null 2>&1 \
>   && echo "CÓ, lùi được" || echo "KHÔNG có tag này — không lùi được về mốc đó"
> ```

```bash
# 1. Tìm SHA của bản chạy tốt gần nhất — hỏi chính image ĐANG chạy, đừng đoán theo thời gian
docker inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' \
  gateway-main authservice-main interviewservice-main campaignservice-main paymentservice-main aiapi-main

# 2. Ghim tag trong .env cạnh compose (server)
echo 'IMAGE_TAG=main-<sha-tốt>' >> ~/docker/main/.env

# 3. Áp
cd ~/docker/main && docker compose pull && docker compose up -d
```

Mất vài chục giây, **không phải chờ CI build lại ~15 phút** từ một commit revert.
Quay về bản mới nhất: xoá dòng `IMAGE_TAG` (mặc định `${IMAGE_TAG:-main}` cho lại `:main`).

⚠ **ĐIỀU KIỆN TIÊN QUYẾT:** file compose **TRÊN SERVER** (`~/docker/main/docker-compose.yml`)
phải đã tham số hoá `${IMAGE_TAG:-main}` y hệt `deploy/compose.yaml` trong repo. Sửa mỗi bên
repo **không tự động** làm được gì cả (xem §0 — hai file là hai artifact khác nhau).
Kiểm nhanh: `grep 'image: ghcr' ~/docker/main/docker-compose.yml` — phải thấy `${IMAGE_TAG`.

⚠ Lùi **một** service lẻ thì sửa thẳng dòng `image:` của service đó, vì `IMAGE_TAG` áp cho
**cả 6**. Lùi cả 6 về cùng một SHA là đường an toàn hơn — các service gọi nhau qua hợp đồng
nội bộ (callback, tên field JSON) và những hợp đồng đó đổi theo commit.

### 9.2 Lùi SCHEMA — 🔴 KHÔNG TỒN TẠI, đừng lên kế hoạch dựa vào nó

`Down()` của EF **không phải** máy thời gian. `Down()` của một `DropColumn` chỉ
`AddColumn` lại — **cột rỗng**. Dữ liệu trong cột đã bị xoá lúc `Up()` chạy và **mất vĩnh
viễn**. Tương tự với `DropTable` và phần lớn `Rename*`/`Alter*` có chuyển kiểu.

Trên production hiện đã có **hàng chục migration destructive** (drop cột chết, tách bảng,
đổi tên). Với chúng, rollback schema **không phải là chậm — nó không tồn tại**.

⇒ **Đường đi thực tế là FIX-FORWARD:** viết migration mới sửa tiếp, không lùi.
⇒ Hệ quả cho §9.1: lùi code qua một mốc có migration destructive thì **code cũ gặp schema
mới** → `42703 column does not exist` → 500 hàng loạt (đã xảy ra **2 lần**: 02/08 và 05/08,
theo chiều ngược lại). **Kiểm `__EFMigrationsHistory` trước khi lùi**, và nếu có migration
nằm giữa hai mốc thì lùi code KHÔNG an toàn — phải fix-forward.

```bash
# migration nào đã apply, mốc nào (chạy cho từng db: isas · isas_interview · isas_campaign · isas_payment)
docker exec -i postgres-main psql -v ON_ERROR_STOP=1 -U admin -d <db> \
  -c 'SELECT * FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 5;'
```
⚠ Tên cột lịch sử **không đồng nhất**: Auth dùng `"MigrationId"`, ba service kia dùng
`migration_id` (tên bảng thì đều là `"__EFMigrationsHistory"`).

### 9.3 Chốt lại

| Muốn lùi | Làm được? | Cách |
|---|---|---|
| Code (image) | ✅ vài chục giây | `IMAGE_TAG=main-<sha>` + `up -d` (§9.1) |
| Schema (migration destructive) | ❌ **không** | fix-forward bằng migration mới (§9.2) |
| Cấu hình / feature flag | ✅ nhanh nhất | đổi env + `up -d`; phần lớn cờ đã thiết kế để tắt được mà không cần deploy lại |

⚠ **Không có backup DB nào trong repo** — `deploy/compose.yaml`, `compose.yaml`, `ci.yml` và
`scripts/` đều **0 hit** cho `pg_dump`/`backup` (kiểm 2026-08-06). *(Chưa xác minh server có
cron riêng ngoài repo hay không — nếu có thì bổ sung vào đây.)* Nghĩa là trước một
apply-window rủi ro, `pg_dump` **chạy tay** là lưới an toàn duy nhất, và nó không nằm trong
quy trình nào cả — phải nhớ mà làm:

```bash
docker exec postgres-main pg_dump -U admin -d <db> -Fc > ~/backup-<db>-$(date +%Y%m%d-%H%M).dump
```
