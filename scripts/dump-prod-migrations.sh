#!/usr/bin/env bash
# Xuất lịch sử migration ĐÃ ÁP của 4 DB production ra stdout, dạng máy đọc được.
#
# Vì sao cần: hai lần (02/08 và 05/08) image mới lên trước khi migration được
# apply ⇒ `42703: column … does not exist` trên đường request thật, `/health`
# vẫn xanh nên không ai biết trong nhiều giờ. Muốn chặn ở CI thì phải so được
# "repo có migration nào" với "DB đã áp migration nào" — script này lấy vế sau.
#
# CHỈ ĐỌC. Không tạo file trên server, không ghi DB:
#   - `PGOPTIONS='-c default_transaction_read_only=on'` ⇒ "chỉ đọc" do CHÍNH
#     Postgres thi hành, không phải lời hứa trong comment. Mọi lệnh ghi lỡ tay
#     lọt vào đây sẽ bị server từ chối.
#   - chạy qua `bash -s` nên nội dung script không nằm lại trên server.
#
# Cách chạy (từ máy dev / runner có SSH tới server):
#
#     ssh user@host 'bash -s' < scripts/dump-prod-migrations.sh > prod-migrations.txt
#     python3 scripts/check-schema-gate.py prod-migrations.txt
#
# Giao thức stdout — phân biệt được "DB không có migration nào" với "không đọc
# được DB", hai ca khác hẳn nhau về mức nghiêm trọng (cái sau KHÔNG được coi là
# an toàn, xem check-schema-gate.py):
#
#     #db  <service> <database> <cột-MigrationId>
#     <service>|<migration-id>
#     #err <service> <database> <lý-do>

set -euo pipefail

CONTAINER="${PG_CONTAINER:-postgres-main}"
PGUSER_NAME="${PG_USER:-admin}"

# service:database — 4 DB-per-service (GEN-2).
PAIRS=(
    "auth:isas"
    "interview:isas_interview"
    "campaign:isas_campaign"
    "payment:isas_payment"
)

# Chạy psql trong container, ép read-only ở tầng server.
psql_ro() {
    local db="$1" sql="$2"
    docker exec -i \
        -e PGOPTIONS='-c default_transaction_read_only=on' \
        "$CONTAINER" \
        psql -U "$PGUSER_NAME" -d "$db" -At -v ON_ERROR_STOP=1 -c "$sql"
}

for pair in "${PAIRS[@]}"; do
    service="${pair%%:*}"
    database="${pair#*:}"

    # Tên cột KHÁC NHAU giữa các service: Auth dùng "MigrationId" (PascalCase,
    # bắt buộc nháy kép), 3 service kia dùng migration_id. TRA, KHÔNG ĐOÁN —
    # đoán sai thì psql báo lỗi cột và ta mất luôn cả DB đó khỏi bản dump.
    if ! column="$(psql_ro "$database" "
        SELECT column_name
        FROM information_schema.columns
        WHERE table_name = '__EFMigrationsHistory'
          AND column_name ILIKE 'migration%id'
        LIMIT 1;" 2>/dev/null)"; then
        echo "#err $service $database khong-ket-noi-duoc"
        continue
    fi

    if [ -z "$column" ]; then
        echo "#err $service $database khong-tim-thay-__EFMigrationsHistory"
        continue
    fi

    echo "#db $service $database $column"

    if ! rows="$(psql_ro "$database" "
        SELECT \"$column\"
        FROM \"__EFMigrationsHistory\"
        ORDER BY \"$column\";" 2>/dev/null)"; then
        echo "#err $service $database khong-doc-duoc-lich-su"
        continue
    fi

    # `rows` rỗng = DB chưa áp migration nào. Đó là dữ liệu hợp lệ (marker #db ở
    # trên đã chứng minh bảng tồn tại), KHÔNG phải lỗi ⇒ không in #err.
    while IFS= read -r migration_id; do
        [ -z "$migration_id" ] && continue
        echo "$service|$migration_id"
    done <<< "$rows"
done
