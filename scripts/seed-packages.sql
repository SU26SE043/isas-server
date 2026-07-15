-- seed-packages.sql — seed catalog gói credit prepaid (product_packages) cho PaymentService.
-- DB server KHÔNG seed product_packages tự động → catalog rỗng → GET /payment/package trả []
-- → không mua credit được. Chạy file này TRƯỚC khi demo luồng tiền.
--
-- Chạy trên server (DB Payment `isas_payment`):
--   docker exec -i postgres-main psql -U admin -d isas_payment < scripts/seed-packages.sql
--
-- Cột khớp migration InitialCreate (src/services/Isas.PaymentService/Migrations):
--   id uuid PK (default gen_random_uuid) · name text · type integer (1=OneTime,2=Subscription)
--   price_vnd integer · interview_credits integer? · duration_days integer? (NULL cho OneTime)
--   is_active boolean (default true) · created_at timestamptz (default now())
-- ⚠ type/price_vnd lưu INTEGER (enum Payment serialize số; giá VND lẻ nằm trong trần int).
--
-- Idempotent: guard theo name (không có UNIQUE trên name) → chạy lại KHÔNG nhân đôi.

INSERT INTO product_packages (name, type, price_vnd, interview_credits, duration_days, is_active)
SELECT v.name, v.type, v.price_vnd, v.interview_credits, v.duration_days, v.is_active
FROM (VALUES
    ('Starter — 5 credits',   1,  99000,  5,  NULL::int, true),
    ('Standard — 10 credits', 1, 189000, 10,  NULL::int, true),
    ('Pro — 20 credits',      1, 349000, 20,  NULL::int, true)
) AS v(name, type, price_vnd, interview_credits, duration_days, is_active)
WHERE NOT EXISTS (
    SELECT 1 FROM product_packages p WHERE p.name = v.name
);

-- Kiểm tra:
SELECT name, type, price_vnd, interview_credits, is_active
FROM product_packages
WHERE type = 1
ORDER BY price_vnd;
