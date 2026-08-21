#!/usr/bin/env bash
# ══════════════════════════════════════════════════════════════════════════════
# Triển khai nhánh `dev` thẳng lên stack dev trên server — KHÔNG qua GitHub Actions.
#
# Vì sao có script này: một vòng CI mất ~10 phút (test 5,0' · AIService 1,1' ·
# build+push 4,1' · deploy 1,1'), mà phần đắt nhất là ĐẨY ẢNH LÊN REGISTRY RỒI KÉO
# NGƯỢC VỀ CÙNG MỘT MÁY CHỦ — khoảng 2GB đi vòng qua Internet hai lần cho không.
# Build tại chỗ bỏ hẳn vòng đó: server 8 core, có sẵn .NET SDK, cùng kiến trúc
# x86_64 nên không phải emulate.
#
# Nhanh thêm nhờ: chỉ build service THẬT SỰ ĐỔI, và chỉ đồng bộ mã nguồn (vài trăm
# KB) thay vì ảnh.
#
# ⚠ KHÔNG dùng cho `main`. Production đi qua CI để còn test, guard migration và
#   schema gate làm lưới. Script này cố ý bỏ qua chúng để đổi lấy tốc độ — hợp lý
#   với môi trường thử nghiệm, không hợp lý với production.
#
# Dùng:
#   scripts/deploy-dev.sh                  # tự dò service đã đổi so với bản ĐANG CHẠY
#   scripts/deploy-dev.sh interview ai     # chỉ định thẳng
#   scripts/deploy-dev.sh all              # build lại tất cả
#
# Viết cho bash 3.2 (mặc định của macOS) — không dùng mảng kết hợp.
# ══════════════════════════════════════════════════════════════════════════════
set -euo pipefail

SERVER="${ISAS_SERVER:-duc2834@100.64.204.33}"
REMOTE_SRC='$HOME/src/isas-server'      # nháy đơn: để SERVER tự expand, không phải máy này
STACK_DIR='$HOME/docker/dev'
GATEWAY_PORT=5051                        # main giữ 5050
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ALL="auth interview campaign payment gateway ai"

dockerfile_of() { case "$1" in
  auth)      echo src/services/Isas.AuthService/Dockerfile ;;
  interview) echo src/services/Isas.InterviewService/Dockerfile ;;
  campaign)  echo src/services/Isas.CampaignService/Dockerfile ;;
  payment)   echo src/services/Isas.PaymentService/Dockerfile ;;
  gateway)   echo src/gateway/Isas.Gateway/Dockerfile ;;
  ai)        echo src/services/Isas.AIService/Dockerfile ;;
esac; }
image_of() { case "$1" in
  auth)      echo ghcr.io/su26se043/isas.authservice ;;
  interview) echo ghcr.io/su26se043/isas.interviewservice ;;
  campaign)  echo ghcr.io/su26se043/isas.campaignservice ;;
  payment)   echo ghcr.io/su26se043/isas.paymentservice ;;
  gateway)   echo ghcr.io/su26se043/isas.gateway ;;
  ai)        echo ghcr.io/su26se043/isas.aiservice ;;
esac; }
watch_of() { case "$1" in
  auth)      echo src/services/Isas.AuthService ;;
  interview) echo src/services/Isas.InterviewService ;;
  campaign)  echo src/services/Isas.CampaignService ;;
  payment)   echo src/services/Isas.PaymentService ;;
  gateway)   echo src/gateway/Isas.Gateway ;;
  ai)        echo src/services/Isas.AIService ;;
esac; }
# Build context KHÔNG đồng nhất giữa các service — phải khớp ĐÚNG những gì CI dùng, nếu không
# ảnh build tay sẽ khác ảnh CI ở chỗ không ai ngờ. Service .NET COPY từ gốc repo (cần cả
# `src/shared`) nên context = `.`; AIService là cây Python độc lập, Dockerfile của nó `COPY app ./app`
# nên context phải là chính thư mục service — dùng `.` ở đây thì `/app` không tồn tại và build chết.
context_of() { case "$1" in
  ai) echo src/services/Isas.AIService ;;
  *)  echo . ;;
esac; }

container_of() { case "$1" in
  auth)      echo authservice-dev ;;
  interview) echo interviewservice-dev ;;
  campaign)  echo campaignservice-dev ;;
  payment)   echo paymentservice-dev ;;
  gateway)   echo gateway-dev ;;
  ai)        echo aiapi-dev ;;
esac; }

# Thư viện dùng chung: đổi ở đây thì MỌI service .NET phải build lại. Bỏ sót vế này là
# cách âm thầm triển khai một bản KHÔNG chứa thay đổi mình vừa viết.
SHARED=src/shared

cd "$REPO_ROOT"
SHA="$(git rev-parse HEAD)"
SHORT="$(echo "$SHA" | cut -c1-12)"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"

if [ "$BRANCH" = "main" ]; then
  echo "❌ Script này KHÔNG dùng cho main — production phải đi qua CI để còn test/guard." >&2
  exit 1
fi
if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "⚠  Có thay đổi CHƯA COMMIT. Ảnh sẽ mang nhãn revision $SHORT nhưng nội dung khác nó."
  echo "   Commit trước, hoặc chấp nhận nhãn nói sai. Ctrl-C trong 4s để dừng."
  sleep 4
fi

# ── Chọn service cần build ────────────────────────────────────────────────────
TARGETS=""
if [ "${1:-}" = "all" ]; then
  TARGETS="$ALL"
elif [ $# -gt 0 ]; then
  TARGETS="$*"
else
  # Mốc so sánh = revision ĐANG CHẠY trên server, KHÔNG phải HEAD~1: nếu lần triển khai
  # trước thất bại hoặc bị bỏ qua thì HEAD~1 sẽ báo "không có gì đổi" trong khi server vẫn cũ.
  PREV="$(ssh -o ConnectTimeout=10 "$SERVER" \
    "docker inspect interviewservice-dev --format '{{index .Config.Labels \"org.opencontainers.image.revision\"}}' 2>/dev/null" 2>/dev/null | tr -d '\r' || true)"
  if [ -z "$PREV" ] || ! git cat-file -e "${PREV}^{commit}" 2>/dev/null; then
    echo "ℹ  Không đọc được revision đang chạy (hoặc nó không có trong repo) → build TẤT CẢ."
    TARGETS="$ALL"
  elif [ "$PREV" = "$SHA" ]; then
    echo "✅ Server đã chạy đúng $SHORT — không cần làm gì."
    exit 0
  else
    CHANGED="$(git diff --name-only "$PREV" HEAD)"
    if echo "$CHANGED" | grep -q "^$SHARED/"; then
      echo "ℹ  $SHARED đổi → mọi service .NET build lại."
      TARGETS="auth interview campaign payment gateway"
      echo "$CHANGED" | grep -q "^$(watch_of ai)/" && TARGETS="$TARGETS ai"
    else
      for k in $ALL; do
        echo "$CHANGED" | grep -q "^$(watch_of "$k")/" && TARGETS="$TARGETS $k"
      done
      TARGETS="$(echo "$TARGETS" | xargs || true)"
    fi
    if [ -z "$TARGETS" ]; then
      echo "✅ Không service nào đổi so với $(echo "$PREV" | cut -c1-12) — không cần build."
      exit 0
    fi
  fi
fi

echo "═══ Deploy dev · $SHORT · build: $TARGETS ═══"

# ── 1. Đồng bộ mã nguồn ───────────────────────────────────────────────────────
echo "→ đồng bộ mã nguồn…"
ssh -o ConnectTimeout=10 "$SERVER" "mkdir -p $REMOTE_SRC"
rsync -az --delete \
  --exclude '.git' --exclude 'bin/' --exclude 'obj/' \
  --exclude 'node_modules' --exclude '.claude' --exclude '__pycache__/' \
  --exclude '.venv/' --exclude 'test-results' \
  "$REPO_ROOT/" "$SERVER:$REMOTE_SRC/"

# ── 2. Build tại chỗ ──────────────────────────────────────────────────────────
# Nhãn `org.opencontainers.image.revision` đặt GIỐNG HỆT CI: mọi phép soi "container
# đang chạy commit nào" đọc đúng nhãn này. Thiếu nó thì không phân biệt được ảnh build
# tay với ảnh từ CI, và bước kiểm cuối script cũng mất căn cứ.
for k in $TARGETS; do
  df="$(dockerfile_of "$k")"
  if [ -z "$df" ]; then echo "❌ Không biết service '$k'. Hợp lệ: $ALL" >&2; exit 1; fi
  echo "→ build $k…"
  ssh -o ConnectTimeout=10 "$SERVER" \
    "cd $REMOTE_SRC && docker build -f '$df' -t '$(image_of "$k"):dev' \
       --label 'org.opencontainers.image.revision=$SHA' -q '$(context_of "$k")' >/dev/null" \
    || { echo "❌ build $k THẤT BẠI." >&2; exit 1; }
done

# ── 3. Khởi động lại stack ────────────────────────────────────────────────────
# Ghim IMAGE_TAG=dev: ảnh vừa build mang đúng tag đó. Nếu `.env` còn ghim `dev-<sha>`
# từ một lần CI deploy cũ thì compose sẽ kéo ảnh CŨ về và ta triển khai nhầm bản.
echo "→ khởi động lại stack…"
ssh -o ConnectTimeout=30 "$SERVER" "
  cd $STACK_DIR
  if grep -q '^IMAGE_TAG=' .env; then sed -i 's|^IMAGE_TAG=.*|IMAGE_TAG=dev|' .env
  else echo 'IMAGE_TAG=dev' >> .env; fi
  docker compose up -d --remove-orphans 2>&1 | grep -E 'Recreated|Started|Error' || true
"

# ── 4. Kiểm bằng HÀNH VI, không bằng dấu thời gian ────────────────────────────
echo "→ kiểm…"
sleep 8
FAIL=0
for k in $TARGETS; do
  c="$(container_of "$k")"
  rev="$(ssh -o ConnectTimeout=10 "$SERVER" \
    "docker inspect $c --format '{{index .Config.Labels \"org.opencontainers.image.revision\"}}' 2>/dev/null" 2>/dev/null | tr -d '\r' || true)"
  if [ "$rev" = "$SHA" ]; then
    printf '   ✅ %-22s %s\n' "$c" "$SHORT"
  else
    printf '   ❌ %-22s chạy %s, kỳ vọng %s\n' "$c" "$(echo "${rev:-KHÔNG-ĐỌC-ĐƯỢC}" | cut -c1-12)" "$SHORT"
    FAIL=1
  fi
done

# Gateway trả lời được mới tính là xong. `up -d` chỉ nói "container đã được tạo".
code="$(ssh -o ConnectTimeout=30 "$SERVER" \
  "curl -s -o /dev/null -w '%{http_code}' -m 25 http://localhost:$GATEWAY_PORT/api/v1/payment/package" 2>/dev/null | tr -d '\r' || true)"
if [ "$code" = "200" ]; then echo "   ✅ gateway :$GATEWAY_PORT → 200"
else echo "   ❌ gateway :$GATEWAY_PORT → ${code:-không trả lời}"; FAIL=1; fi

if [ "$FAIL" -eq 0 ]; then echo "═══ XONG ═══"; else echo "═══ CÓ LỖI ═══" >&2; exit 1; fi
