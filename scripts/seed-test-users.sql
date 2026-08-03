-- seed-test-users.sql — đặt password Test@123456 cho account test KHÔNG tạo được qua API.
-- Chạy trên server (DB Auth `isas`):
--   docker exec -i postgres-main psql -U admin -d isas < scripts/seed-test-users.sql
--
-- Bộ account test chuẩn (xem docs): tất cả password Test@123456
--   candidate@isas.local  Candidate            → tạo qua POST /auth/register (đã có API)
--   employer@isas.local   Employer + OrgAdmin  → tạo qua POST /auth/register-org (đã có API)
--   hr@isas.local         Employer + HrMember  → tạo qua POST /auth/org/members (A6) nhưng PASSWORDLESS
--   admin@isas.local      Admin                → không có API tạo (seed-admin.sql, password prod riêng)
--
-- File này chỉ UPDATE password_hash cho hr@ + admin@ (2 account API không set password được).
-- Hash = ASP.NET Core Identity v3 (PBKDF2-HMAC-SHA512, 100k iter) của "Test@123456".
-- Idempotent: chạy lại vô hại. KHÔNG đụng account khác.

UPDATE users
SET password_hash = 'AQAAAAIAAYagAAAAEIjGOcZe0YS2SPFOjXToOz11OLRXt9tR8YHba38D9tq6KcJXcArRGkzbtXZWSscaxQ=='
WHERE email IN ('hr@isas.local', 'admin@isas.local');

-- Kiểm tra:
SELECT email,
       CASE WHEN password_hash IS NULL THEN 'PASSWORDLESS' ELSE 'có password' END AS pw
FROM users
WHERE email IN ('candidate@isas.local','employer@isas.local','hr@isas.local','admin@isas.local')
ORDER BY email;
