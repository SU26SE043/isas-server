#!/usr/bin/env bash
# ══════════════════════════════════════════════════════════════════════════════
# Triển khai nhánh `dev` thẳng lên stack dev trên server — KHÔNG qua GitHub Actions.
#
# Vì sao có script này: đường CI mất ~10 phút cho một vòng (test 5' · build+push 4,1' ·
# deploy 1,1'), trong đó phần đắt nhất là ĐẨY ẢNH LÊN REGISTRY RỒI KÉO VỀ — cùng một
# máy chủ, cùng kiến trúc, mà dữ liệu đi vòng qua Internet hai lần. Build TẠI CHỖ bỏ
# hẳn vòng đó.
#
# Nhanh thêm nhờ hai điều:
#   • CHỈ build service thật sự đổi (mặc định script tự dò theo git diff), thay vì cả 6
#   • Chỉ đồng bộ mã nguồn qua rsync (vài trăm KB) thay vì ảnh (~2GB)
#
# ⚠ KHÔNG dùng cho `main`. Production đi qua CI để còn có test, guard migration và
#   schema gate làm lưới. Script này cố ý bỏ qua chúng để đổi lấy tốc độ — hợp lý với
#   một môi trường thử nghiệm, không hợp lý với production.
#
# Dùng:
#   scripts/deploy-dev.sh                  # tự dò service đã đổi so với lần deploy trước
#   scripts/deploy-dev.sh interview ai     # chỉ định thẳng
#   scripts/deploy-dev.sh all              # build lại tất cả
# ══════════════════════════════════════════════════════════════════════════════
set -euo pipefail

SERVER="${ISAS_SERVER:-duc2834@100.64.204.33}"
REMOTE_SRC="\$HOME/src/isas-server"
STACK_DIR="\$HOME/docker/dev"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# service → (đường dẫn Dockerfile, tên ảnh, thư mục quyết định "có đổi hay không")
declare -A DOCKERFILE=(
  [auth]=src/services/Isas.AuthService/Dockerfile
  [interview]=src/services/Isas.InterviewService/Dockerfile
  [campaign]=src/services/Isas.CampaignService/Dockerfile
  [payment]=src/services/Isas.PaymentService/Dockerfile
  [gateway]=src/gateway/Isas.Gateway/Dockerfile
  [ai]=src/services/Isas.AIService/Dockerfile
)
declare -A IMAGE=(
  [auth]=ghcr.io/su26se043/isas.authservice
  [interview]=ghcr.io/su26se043/isas.interviewservice
  [campaign]=ghcr.io/su26se043/isas.campaignservice
  [payment]=ghcr.io/su26se043/isas.paymentservice
  [gateway]=ghcr.io/su26se043/isas.gateway
  [ai]=ghcr.io/su26se043/isas.aiservice
)
declare -A WATCH=(
  [auth]=src/services/Isas.AuthService
  [interview]=src/services/Isas.InterviewService
  [campaign]=src/services/Isas.CampaignService
  [payment]=src/services/Isas.PaymentService
  [gateway]=src/gateway/Isas.Gateway
  [ai]=src/services/Isas.AIService
)
# Thư viện dùng chung: đổi ở đây thì MỌI service .NET phải build lại. Bỏ sót vế này là
# cách âm thầm triển khai một bản không chứa thay đổi mình vừa viết.
SHARED=src/shared

cd "$REPO_ROOT"
SHA="$(git rev-parse HEAD)"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"

if [ "$BRANCH" = "main" ]; then
  echo "❌ Script này KHÔNG dùng cho main — production phải đi qua CI để còn test/guard." >&2
  exit 1
fi
if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "⚠  Có thay đổi CHƯA COMMIT. Ảnh sẽ mang nhãn revision $SHA nhưng nội dung khác nó."
  echo "   Commit trước, hoặc chấp nhận nhãn nói sai. Ctrl-C để dừng."
  sleep 4
fi

# ── Chọn service cần build ────────────────────────────────────────────────────
TARGETS=()
if [ $# -gt 0 ] && [ "${1:-}" != "all" ]; then
  TARGETS=("$@")
elif [ "${1:-}" = "all" ]; then
  TARGETS=(auth interview campaign payment gateway ai)
else
  # Mốc so sánh = revision ĐANG CHẠY trên server, không phải HEAD~1: nếu lần deploy trước
  # thất bại hoặc bị bỏ qua thì HEAD~1 sẽ báo "không có gì đổi" trong khi server vẫn cũ.
  PREV="$(ssh -o ConnectTimeout=10 "$SERVER" \
    "docker inspect interviewservice-dev --format '{{index .Config.Labels \"org.opencontainers.image.revision\"}}' 2>/dev/null" || true)"
  PREV="$(echo "$PREV" | tr -d '\r')"
  if [ -z "$PREV" ] || ! git cat-file -e "$PREV^{commit}" 2>/dev/null; then
    echo "ℹ  Không đọc được revision đang chạy → build TẤT CẢ."
    TARGETS=(auth interview campaign payment gateway ai)
  else
    CHANGED="$(git diff --name-only "$PREV" HEAD)"
    if echo "$CHANGED" | grep -q "^$SHARED/"; then
      echo "ℹ  $SHARED đổi → mọi service .NET phải build lại."
      TARGETS=(auth interview campaign payment gateway)
      echo "$CHANGED" | grep -q "^${WATCH[ai]}/" && TARGETS+=(ai)
    else
      for k in auth interview campaign payment gateway ai; do
        echo "$CHANGED" | grep -q "^${WATCH[$k]}/" && TARGETS+=("$k")
      done
    fi
    [ ${#TARGETS[@]} -eq 0 ] && { echo "✅ Không service nào đổi so với $(echo "$PREV" | cut -c1-12) — không cần deploy."; exit 0; }
  fi
fi

echo "═══ Deploy dev · $(echo "$SHA" | cut -c1-12) · build: ${TARGETS[*]} ═══"

# ── 1. Đồng bộ mã nguồn ───────────────────────────────────────────────────────
echo "→ đồng bộ mã nguồn…"
ssh -o ConnectTimeout=10 "$SERVER" "mkdir -p $REMOTE_SRC"
rsync -az --delete \
  --exclude '.git' --exclude '**/bin/' --exclude '**/obj/' \
  --exclude 'node_modules' --exclude '.claude' --exclude '**/__pycache__/' \
  --exclude '**/.venv/' --exclude 'test-results' \
  "$REPO_ROOT/" "$SERVER:$REMOTE_SRC/"

# ── 2. Build tại chỗ ──────────────────────────────────────────────────────────
# Nhãn `org.opencontainers.image.revision` phải giống hệt CI đặt: bước kiểm sau deploy
# (và mọi lần soi "container đang chạy commit nào") đọc đúng nhãn này. Thiếu nó thì
# không còn cách nào phân biệt ảnh build tay với ảnh build từ CI.
for k in "${TARGETS[@]}"; do
  [ -z "${DOCKERFILE[$k]:-}" ] && { echo "❌ Không biết service '$k'." >&2; exit 1; }
  echo "→ build $k…"
  ssh -o ConnectTimeout=10 "$SERVER" "cd $REMOTE_SRC && docker build \
    -f '${DOCKERFILE[$k]}' \
    -t '${IMAGE[$k]}:dev' \
    --label 'org.opencontainers.image.revision=$SHA' \
    -q . >/dev/null" || { echo "❌ build $k THẤT BẠI." >&2; exit 1; }
done

# ── 3. Khởi động lại stack ────────────────────────────────────────────────────
# Ghim IMAGE_TAG=dev: ảnh vừa build mang đúng tag đó. Nếu .env còn ghim `dev-<sha>` từ
# một lần CI deploy cũ thì compose sẽ kéo ảnh CŨ về và ta triển khai nhầm bản.
echo "→ khởi động lại stack…"
ssh -o ConnectTimeout=20 "$SERVER" "
  cd $STACK_DIR
  if grep -q '^IMAGE_TAG=' .env; then sed -i 's|^IMAGE_TAG=.*|IMAGE_TAG=dev|' .env; else echo 'IMAGE_TAG=dev' >> .env; fi
  docker compose up -d --remove-orphans 2>&1 | grep -E 'Recreated|Started|Error' || true
"

# ── 4. Kiểm bằng HÀNH VI, không bằng dấu thời gian ────────────────────────────
echo "→ kiểm…"
sleep 8
FAIL=0
for k in "${TARGETS[@]}"; do
  case "$k" in
    auth) c=authservice-dev ;; interview) c=interviewservice-dev ;;
    campaign) c=campaignservice-dev ;; payment) c=paymentservice-dev ;;
    gateway) c=gateway-dev ;; ai) c=aiapi-dev ;;
  esac
  rev="$(ssh -o ConnectTimeout=10 "$SERVER" \
    "docker inspect $c --format '{{index .Config.Labels \"org.opencontainers.image.revision\"}}' 2>/dev/null" | tr -d '\r')"
  if [ "$rev" = "$SHA" ]; then printf '   ✅ %-22s %s\n' "$c" "$(echo "$rev" | cut -c1-12)"
  else printf '   ❌ %-22s chạy %s, kỳ vọng %s\n' "$c" "$(echo "${rev:-KHÔNG-ĐỌC-ĐƯỢC}" | cut -c1-12)" "$(echo "$SHA" | cut -c1-12)"; FAIL=1; fi
done

# Gateway trả lời được mới tính là deploy xong. `up -d` chỉ nói "container đã được tạo".
code="$(ssh -o ConnectTimeout=20 "$SERVER" "curl -s -o /dev/null -w '%{http_code}' -m 25 http://localhost:5051/api/v1/payment/package" | tr -d '\r')"
[ "$code" = "200" ] && echo "   ✅ gateway :5051 → 200" || { echo "   ❌ gateway :5051 → $code"; FAIL=1; }

[ "$FAIL" -eq 0 ] && echo "═══ XONG ═══" || { echo "═══ CÓ LỖI ═══" >&2; exit 1; }
