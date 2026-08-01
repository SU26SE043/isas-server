-- seed-packages.sql — seed catalog gói credit prepaid (product_packages) cho PaymentService.
-- DB server KHÔNG seed product_packages tự động → catalog rỗng → GET /payment/package trả []
-- → không mua credit được. Chạy file này TRƯỚC khi demo luồng tiền.
--
-- Chạy trên server (DB Payment `isas_payment`):
--   docker exec -i postgres-main psql -U admin -d isas_payment < scripts/seed-packages.sql
--
-- Cột khớp schema hiện hành (src/services/Isas.PaymentService/Migrations):
--   id uuid PK (default gen_random_uuid) · name text · type varchar ('OneTime'|'Subscription')
--   price_vnd bigint · interview_credits integer? · duration_days integer? (NULL cho OneTime)
--   is_active boolean (default true) · created_at timestamptz (default now())
-- ⚠ DB lưu enum dạng chuỗi (JSON API mới trả enum dạng số); price_vnd là bigint.
--
-- Idempotent: guard theo name (không có UNIQUE trên name) → chạy lại KHÔNG nhân đôi.

INSERT INTO product_packages (name, type, price_vnd, interview_credits, duration_days, is_active)
SELECT v.name, v.type, v.price_vnd, v.interview_credits, v.duration_days, v.is_active
FROM (VALUES
    ('Starter — 5 credits',   'OneTime',  99000::bigint,  5,  NULL::int, true),
    ('Standard — 10 credits', 'OneTime', 189000::bigint, 10,  NULL::int, true),
    ('Pro — 20 credits',      'OneTime', 349000::bigint, 20,  NULL::int, true)
) AS v(name, type, price_vnd, interview_credits, duration_days, is_active)
WHERE NOT EXISTS (
    SELECT 1 FROM product_packages p WHERE p.name = v.name
);

-- Kiểm tra:
SELECT name, type, price_vnd, interview_credits, is_active
FROM product_packages
WHERE type = 'OneTime'
ORDER BY price_vnd;
