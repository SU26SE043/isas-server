-- backfill-rubric-scoring-scope.sql — SC2
-- Gán lại `rubric_criteria.scoring_scope` cho RUBRIC RIÊNG của ứng viên (BC16) bằng cách kế thừa
-- phạm vi chấm của tiêu chí BỘ CHUẨN cùng (job_category, language, name).
--
-- ⚠ CHẠY TAY, KHÔNG nhét vào migration tự động. Đây là dữ liệu do người dùng tạo, không phải seed.
-- Chạy trên server (DB Interview `isas_interview`):
--   docker exec -i postgres-main psql -U admin -d isas_interview -f - < scripts/backfill-rubric-scoring-scope.sql
--
-- ═══════════════════════════════════════════════════════════════════════════════════════════════
-- VÌ SAO CẦN
-- ═══════════════════════════════════════════════════════════════════════════════════════════════
-- `RubricLibraryService.ReplaceAsync` (trước bản vá SC2) tạo tiêu chí rubric riêng mà KHÔNG set
-- `ScoringScope` ⇒ rơi về default của EF là 'Always'. Chuỗi nhân quả đã truy trên production:
--   1. rubric riêng BA/vi: 7/7 dòng 'Always' · BE/vi: 9/9 dòng 'Always' — KHÔNG một dòng
--      'WhenTargeted' nào. Bộ chuẩn cùng nghề thì đúng chuẩn: 4 'Always' (tiêu chí CÁCH NÓI)
--      + 3 'WhenTargeted' (tiêu chí NỘI DUNG).
--   2. `PracticeService.LoadTargetableCriteriaAsync` lọc đúng 'WhenTargeted' ⇒ trả RỖNG ⇒ nhánh
--      `if (targetable.Count > 0)` trượt ⇒ gọi overload `GenerateQuestionsAsync` KHÔNG kèm criteria
--      ⇒ AIService không gắn nhãn ⇒ `practice_questions.target_criterion_ids = NULL`.
--   3. `ScoringScopeFilter.Apply` gặp nhãn NULL thì trả NGUYÊN bộ tiêu chí (lùi an toàn) ⇒ mọi câu
--      trả lời bị chấm trên TOÀN BỘ rubric, kể cả tiêu chí câu hỏi không hề hỏi tới.
--   4. Đo được: 400/593 câu hỏi (67%) trắng nhãn; hỏng THEO CẢ BUỔI — 37/96 buổi (39%) trắng sạch,
--      0 buổi nửa nọ nửa kia; và chỉ đúng hai (nghề, ngôn ngữ) CÓ rubric riêng là BA/vi + BE/vi bị
--      dính, FE/vi và BA/en đạt 100%. Đúng chữ ký của một thuộc tính RUBRIC cố định suốt buổi.
--   5. Triệu chứng người dùng thấy: câu đào sâu chỉ hỏi cơ chế xoay vòng refresh token vẫn bị chấm
--      tiêu chí "Thiết kế hệ thống & CSDL" 2–3/5 — chính bộ chấm viết trong nhận xét rằng "câu trả
--      lời tập trung vào cơ chế bảo mật hơn là thiết kế hệ thống tổng thể hay CSDL" rồi VẪN trừ điểm.
--
-- Bản vá nguồn (SC2, `RubricLibraryService`) chỉ sửa những lần LƯU TỪ NAY VỀ SAU. File này sửa
-- 16 dòng ĐANG TỒN TẠI (BA/vi 7 + BE/vi 9). Số dòng THỰC SỰ đổi = số tên trùng với tiêu chí
-- 'WhenTargeted' của bộ chuẩn — SELECT (1) bên dưới in ra con số đó TRƯỚC khi ghi.
--
-- ═══════════════════════════════════════════════════════════════════════════════════════════════
-- ⚠ ĐÁNH ĐỔI: ĐỔI THƯỚC ĐO TRÊN RUBRIC ĐANG ACTIVE MÀ KHÔNG BUMP VERSION
-- ═══════════════════════════════════════════════════════════════════════════════════════════════
-- File này UPDATE tại chỗ các dòng `is_active = true` — tức đổi PHẠM VI CHẤM của bộ tiêu chí mà
-- ứng viên đang dùng, giữ nguyên `version`. Hai phương án và cái giá của mỗi bên:
--
--   (A) BUMP VERSION (deactivate bộ cũ + chèn bộ mới version+1):
--       + Buổi đang chạy dở đã ghim (`practice_sessions.b2c_rubric_owner_id/b2c_rubric_version`)
--         vẫn nạp NGUYÊN bộ cũ — `RubricCriteriaLoader` CỐ Ý không lọc `is_active` ở nhánh đã ghim,
--         nên không có ca PAY-13 (nạp 0 tiêu chí ⇒ answer không bao giờ được chấm).
--       − Đẻ ra một version mà ứng viên KHÔNG hề tạo và KHÔNG hề thấy: API rubric (`RubricCriterionItem`)
--         không phơi `scoring_scope`, nên trong lịch sử phiên bản nó là một bản "y hệt bản trước".
--       − Script phải chèn lại cả `rubric_levels` con của từng tiêu chí (rubric riêng CÓ thể đã khai
--         mốc điểm E9). Chép sót = mất mốc điểm, im lặng. Đây là rủi ro lớn nhất của phương án này.
--
--   (B) UPDATE TẠI CHỖ, KHÔNG BUMP (file này):
--       − Trên lý thuyết: buổi đang chạy dở đổi phạm vi chấm giữa chừng.
--       + Trên THỰC TẾ thì không đổi được gì, và đây là lý do quyết định: cột `scoring_scope` CHỈ có
--         hiệu lực qua `ScoringScopeFilter.Apply`, mà hàm này thoát sớm trả nguyên bộ tiêu chí khi
--         `target_criterion_ids IS NULL`. Mọi câu hỏi của mọi buổi tạo TRƯỚC bản vá đều NULL — đó
--         chính là lỗi đang sửa. Câu đào sâu sinh giữa buổi cũng không cứu được: `AnswerService`
--         chỉ THỪA KẾ nhãn của câu cha (`follow_up`/`clarify`) hoặc để NULL (`new_question`), nó
--         KHÔNG tự tính lại nhãn từ rubric. ⇒ Buổi đang chạy dở không thể đổi thước đo giữa chừng;
--         hiệu lực chỉ bắt đầu từ buổi TẠO MỚI sau khi chạy file này.
--
-- 👉 KHUYẾN NGHỊ: chọn (B) — chính file này. Đổi tại chỗ vừa KHÔNG chạm được vào buổi đang chạy dở
--    (lý do ở trên), vừa không đẻ ra một phiên bản ma trong lịch sử rubric của ứng viên, vừa tránh
--    đường chép `rubric_levels` vốn là chỗ dễ mất dữ liệu nhất của phương án (A).
--    Đổi lại: PHẢI chạy file này SAU khi bản vá SC2 đã deploy, nếu không lần ứng viên bấm Lưu rubric
--    kế tiếp sẽ ghi đè lại toàn bộ bằng 'Always'.
--
-- ═══════════════════════════════════════════════════════════════════════════════════════════════
-- PHẠM VI (câu nào cũng phải tự giới hạn, không dựa vào người chạy nhớ)
-- ═══════════════════════════════════════════════════════════════════════════════════════════════
--   • candidate_id IS NOT NULL  → chỉ rubric RIÊNG của ứng viên (BC16).
--   • campaign_id  IS NULL      → KHÔNG đụng tiêu chí campaign B2B (do HR gõ, cố ý để 'Always' — SC2).
--   • is_active                 → KHÔNG đụng bộ cũ đã bị deactivate (buổi đã ghim chúng vẫn đang
--                                 chấm bằng chúng; sửa = đổi thước đo của một buổi đã xong).
--   • scoring_scope = 'Always'  → chỉ đổi một chiều Always → WhenTargeted ⇒ chạy lại KHÔNG đổi gì
--                                 thêm (idempotent), và số dòng UPDATE đọc được đúng nghĩa.
--   • Khớp tên với bộ chuẩn ACTIVE cùng (job_category, language) có scoring_scope='WhenTargeted'.
--     Chuẩn hoá lower(btrim(...)) — KHÔNG fuzzy, khớp sai còn tệ hơn không khớp. Đây cũng là đúng
--     luật mà bản vá SC2 dùng trong code (Dictionary + StringComparer.OrdinalIgnoreCase trên tên
--     đã Trim), để hai chỗ không thể trả lời khác nhau.
--
-- Tên KHÔNG khớp bộ chuẩn (ứng viên tự thêm tiêu chí mới) → GIỮ 'Always', giống bản vá nguồn:
-- an toàn (vẫn được chấm). SELECT (2) liệt kê riêng nhóm này để người vận hành nhìn thấy chúng.


-- ── (1) TRƯỚC — những dòng SẼ ĐỔI ───────────────────────────────────────────────────────────────
SELECT c.candidate_id,
       c.job_category,
       c.language,
       c.version,
       c.name,
       c.scoring_scope AS scope_hien_tai,
       s.scoring_scope AS scope_theo_bo_chuan
FROM rubric_criteria c
JOIN rubric_criteria s
  ON s.candidate_id IS NULL
 AND s.campaign_id IS NULL
 AND s.is_active
 AND s.job_category = c.job_category
 AND s.language = c.language
 AND lower(btrim(s.name)) = lower(btrim(c.name))
 AND s.scoring_scope = 'WhenTargeted'
WHERE c.candidate_id IS NOT NULL
  AND c.campaign_id IS NULL
  AND c.is_active
  AND c.scoring_scope = 'Always'
ORDER BY c.job_category, c.language, c.candidate_id, c.name;


-- ── (2) TRƯỚC — những dòng KHÔNG khớp bộ chuẩn (giữ 'Always', cần mắt người nhìn) ────────────────
-- Tiêu chí ứng viên tự thêm. Nếu đây là tiêu chí NỘI DUNG thì nó sẽ tiếp tục bị chấm cho MỌI câu
-- hỏi — sai im lặng, chỉ nhìn thấy được ở đây. Không tự đoán hộ: báo lại cho người chốt rubric.
SELECT c.candidate_id,
       c.job_category,
       c.language,
       c.version,
       c.name,
       c.scoring_scope
FROM rubric_criteria c
WHERE c.candidate_id IS NOT NULL
  AND c.campaign_id IS NULL
  AND c.is_active
  AND NOT EXISTS (
        SELECT 1 FROM rubric_criteria s
        WHERE s.candidate_id IS NULL
          AND s.campaign_id IS NULL
          AND s.is_active
          AND s.job_category = c.job_category
          AND s.language = c.language
          AND lower(btrim(s.name)) = lower(btrim(c.name))
      )
ORDER BY c.job_category, c.language, c.candidate_id, c.name;


-- ── (3) TRƯỚC — ảnh chụp tổng thể phạm vi bị đụng tới (đối chiếu với số sau khi ghi) ─────────────
SELECT c.job_category,
       c.language,
       count(*)                                              AS tong_tieu_chi_rieng,
       count(*) FILTER (WHERE c.scoring_scope = 'Always')       AS always_truoc,
       count(*) FILTER (WHERE c.scoring_scope = 'WhenTargeted') AS when_targeted_truoc
FROM rubric_criteria c
WHERE c.candidate_id IS NOT NULL
  AND c.campaign_id IS NULL
  AND c.is_active
GROUP BY c.job_category, c.language
ORDER BY c.job_category, c.language;


-- ── (4) GHI ─────────────────────────────────────────────────────────────────────────────────────
-- Bọc transaction: xem SELECT (5) NGAY TRƯỚC KHI COMMIT. Số không khớp mong đợi → đổi COMMIT thành
-- ROLLBACK và chạy lại. `psql -f` chạy tuần tự nên thứ tự dưới đây là thứ tự thật.
BEGIN;

UPDATE rubric_criteria c
SET scoring_scope = 'WhenTargeted'
FROM rubric_criteria s
WHERE c.candidate_id IS NOT NULL
  AND c.campaign_id IS NULL
  AND c.is_active
  AND c.scoring_scope = 'Always'
  AND s.candidate_id IS NULL
  AND s.campaign_id IS NULL
  AND s.is_active
  AND s.job_category = c.job_category
  AND s.language = c.language
  AND lower(btrim(s.name)) = lower(btrim(c.name))
  AND s.scoring_scope = 'WhenTargeted';


-- ── (5) SAU — kiểm trước khi COMMIT ─────────────────────────────────────────────────────────────
-- Kỳ vọng: mỗi (nghề, ngôn ngữ) có rubric riêng chuyển sang dạng "4 Always + 3 WhenTargeted" giống
-- bộ chuẩn, CỘNG những tiêu chí ứng viên tự thêm (vẫn Always — xem SELECT (2)).
SELECT c.job_category,
       c.language,
       count(*)                                              AS tong_tieu_chi_rieng,
       count(*) FILTER (WHERE c.scoring_scope = 'Always')       AS always_sau,
       count(*) FILTER (WHERE c.scoring_scope = 'WhenTargeted') AS when_targeted_sau
FROM rubric_criteria c
WHERE c.candidate_id IS NOT NULL
  AND c.campaign_id IS NULL
  AND c.is_active
GROUP BY c.job_category, c.language
ORDER BY c.job_category, c.language;

-- Bất biến PHẢI đúng sau backfill: KHÔNG (candidate, nghề, ngôn ngữ) nào còn 0 tiêu chí
-- 'WhenTargeted' trong khi bộ chuẩn cùng (nghề, ngôn ngữ) CÓ tiêu chí 'WhenTargeted'.
-- Truy vấn này phải trả 0 dòng. Còn dòng nào = rubric riêng đó chỉ toàn tên tự đặt ⇒ buổi luyện của
-- người đó vẫn sẽ không có câu hỏi nào được gắn nhãn ⇒ phải xử lý tay (đổi tên hoặc khai scope).
SELECT c.candidate_id, c.job_category, c.language, count(*) AS so_tieu_chi
FROM rubric_criteria c
WHERE c.candidate_id IS NOT NULL
  AND c.campaign_id IS NULL
  AND c.is_active
  AND EXISTS (
        SELECT 1 FROM rubric_criteria s
        WHERE s.candidate_id IS NULL AND s.campaign_id IS NULL AND s.is_active
          AND s.job_category = c.job_category AND s.language = c.language
          AND s.scoring_scope = 'WhenTargeted')
GROUP BY c.candidate_id, c.job_category, c.language
HAVING count(*) FILTER (WHERE c.scoring_scope = 'WhenTargeted') = 0;

COMMIT;
