using Isas.CampaignService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class QuestionItem
    {
        // F10 — id của câu hỏi ĐANG CÓ (echo lại từ `CampaignResponse.Questions[].id`).
        // Có id  → sửa đúng row đó, GIỮ NGUYÊN `source` + `created_at` (câu AI không mất dấu vết, không đổi thứ tự).
        // Không id → câu mới HR gõ tay.
        // Trước F10, PUT questions `Clear()` rồi tạo lại toàn bộ với Guid mới ⇒ sửa 1 câu là xoá sạch
        // provenance `AiGenerated` của cả chiến dịch (F9 sinh bao nhiêu cũng thành CustomHr).
        public Guid? Id { get; set; }

        public string QuestionText { get; set; }

        // ⚠ Server KHÔNG đọc field này khi ghi (create/update đều ép `CustomHr`).
        // `AiGenerated` là KHẲNG ĐỊNH VỀ NGUỒN GỐC — chỉ đường sinh F9 mới có quyền đặt. Nhận từ client thì
        // FE/HR gắn nhãn "AI sinh" cho câu gõ tay được ⇒ field mất sạch giá trị kiểm chứng, mà đó lại đúng
        // là thứ F9/F10 sinh ra để bảo vệ. Giữ lại để không phá hợp đồng JSON đang có (BK20).
        public QuestionSource Source { get; set; }

        // NGÂN HÀNG ĐỀ: true = câu BẮT BUỘC, mọi ứng viên đều gặp; false = nằm trong rổ rút thăm.
        // Chỉ có tác dụng khi campaign đặt `questionsPerSession`; không đặt thì mọi câu đều được hỏi.
        public bool IsRequired { get; set; } = true;

        /// <summary>
        /// Đáp án mẫu. Ba trạng thái, KHÔNG phải hai:
        /// <list type="bullet">
        /// <item><c>null</c> / vắng mặt trong JSON = <b>KHÔNG ĐỔI</b> (giữ nguyên đáp án đang có)</item>
        /// <item>chuỗi rỗng <c>""</c> = <b>XOÁ</b> đáp án</item>
        /// <item>chuỗi có nội dung = ghi đè</item>
        /// </list>
        ///
        /// Bất đối xứng có chủ đích. PUT questions là replace, mà client cũ (FE chưa deploy) không biết
        /// field này nên gửi thiếu ⇒ nếu coi <c>null</c> là "xoá" thì MỘT lần HR bấm Lưu trên bản FE cũ
        /// là mất trắng đáp án của cả chiến dịch. Đúng bẫy F10 đã phải đi vá với <c>source</c>.
        /// Angular textarea rỗng trả <c>''</c> nên FE mới vẫn xoá được tự nhiên.
        /// </summary>
        public string? SampleAnswer { get; set; }

        /// <summary>
        /// Nhóm chủ đề (ngân hàng đề). Cùng hợp đồng ba trạng thái với <see cref="SampleAnswer"/>:
        /// <c>null</c> = không đổi, <c>""</c> = gỡ khỏi nhóm.
        /// </summary>
        public string? QuestionGroup { get; set; }
    }

    // CAMP-16 — một mốc điểm khi GHI. Score nguyên ∈ [0, maxScore của tiêu chí], distinct trong cùng tiêu chí.
    public class CriterionLevelItem
    {
        public int Score { get; set; }
        public string Descriptor { get; set; } = null!;
    }

    // C12: tiêu chí chấm CÓ CẤU TRÚC — HR khai thẳng (name/weight/maxScore/description).
    // Ưu tiên cao nhất (có thì publish bỏ qua AI). Σweight ∈ [0.99,1.01] → chuẩn hoá Σ→1.
    public class CriterionItem
    {
        // RNK1 · HĐ-5 — echo id tiêu chí đang có ⇒ server GIỮ id (update tại chỗ), để snapshot chấm
        // (criterionId) có khoá ỔN ĐỊNH khớp về. Vắng / id lạ ⇒ id mới. FE luôn echo id khi sửa.
        public Guid? Id { get; set; }

        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }   // 0 < weight ≤ 1
        public int MaxScore { get; set; }      // ≥ 1

        // RNK1 · HĐ-5 — điểm sàn % (0..100; null = không sàn; PUT gửi thiếu = null = bỏ sàn). Là luật
        // KẾT LUẬN, KHÔNG bump rubric_version — xem CampaignCriterion.MinPct.
        public int? MinPct { get; set; }

        /// <summary>
        /// CAMP-16 — mốc điểm. BA trạng thái, không phải hai (cùng hợp đồng với
        /// <see cref="QuestionItem.SampleAnswer"/>):
        /// <list type="bullet">
        /// <item><c>null</c> / vắng mặt = <b>KHÔNG ĐỔI</b> — giữ nguyên mốc đang có</item>
        /// <item><c>[]</c> = <b>XOÁ</b> hết mốc (quay về dải mặc định phía Interview)</item>
        /// <item><c>[...]</c> = thay thế toàn bộ</item>
        /// </list>
        ///
        /// <para>Bất đối xứng có chủ đích, và ở đây nó gay gắt hơn <c>SampleAnswer</c>: PUT criteria là
        /// replace-all, nên coi <c>null</c> là "xoá" thì một lần HR bấm Lưu trên bản FE cũ
        /// (chưa biết field này) là mất trắng mốc điểm của cả chiến dịch — mà mất mốc KHÔNG có triệu
        /// chứng: Interview lặng lẽ rơi về dải mặc định và vẫn chấm ra điểm.</para>
        ///
        /// <para>⚠ Carry-over ghép theo <b>tên tiêu chí</b> (case-insensitive). RNK1 · HĐ-5 nay cho
        /// <see cref="Id"/> echo lại được để GIỮ id (khoá ổn định cho snapshot chấm), nhưng carry-over
        /// mốc vẫn theo TÊN. Hệ quả không đổi: ĐỔI TÊN tiêu chí mà không gửi kèm <c>levels</c> thì mốc
        /// MẤT. FE phải luôn gửi <c>levels</c> khi người dùng sửa tên.</para>
        /// </summary>
        public List<CriterionLevelItem>? Levels { get; set; }
    }

    public class CreateCampaignRequest
    {
        [Required]
        public string Title { get; set; }

        [Required]
        public string? Domain { get; set; }
        public string? Language { get; set; }
        public string Seniority { get; set; } = "Junior";

        public int? MaxCandidates { get; set; }

        [Required]
        public int? TimeLimitMinutes { get; set; }

        public bool AntiCheatEnabled { get; set; }

        // SEC-1: bật face-verify (B2B-only, mặc định false). Không gửi → false.
        public bool FaceVerifyEnabled { get; set; }

        // E5: ngưỡng % pass/fail (0–100). null = HR quyết tay (không auto).
        public int? PassScorePct { get; set; }

        // INT-17: bật phỏng vấn THÍCH ỨNG cho chiến dịch (mặc định false = luồng tĩnh). Không gửi → false.
        public bool AdaptiveEnabled { get; set; }
        public bool GroundingEnabled { get; set; }
        // Trần số ứng viên thi ĐỒNG THỜI của chiến dịch. null = không giới hạn.
        // PHẢI >= 1: guard là `running >= max`, nên 0 hoặc số âm làm MỌI lượt Start trả 429
        // ⇒ khoá chiến dịch vĩnh viễn. Xem ValidateConcurrencyCap.
        public int? MaxConcurrentInterviews { get; set; }

        // INT-17: trần câu thích ứng / tổng câu. null = dùng mặc định phía Interview.
        public int? MaxFollowUps { get; set; }
        public int? MaxQuestions { get; set; }
        // INT-17b: trần đào sâu MỖI câu (null/0 = chế độ cũ — đào sâu dồn ở đuôi buổi).
        public int? MaxDeepPerQuestion { get; set; }

        // NGÂN HÀNG ĐỀ: số câu mỗi ứng viên thi, rút từ bộ câu hỏi campaign.
        // null = thi HẾT (hành vi cũ). Đặt số thì phải ≥ 1 — xem ValidateQuestionsPerSession.
        public int? QuestionsPerSession { get; set; }

        // C11: JD & Criteria nhập TEXT trực tiếp (không bắt buộc PDF). Set *_text, *_file_url = null.
        public string? JdText { get; set; }
        public string? CriteriaText { get; set; }

        // C12: tiêu chí structured HR khai thẳng — ưu tiên cao nhất (publish bỏ qua AI). Chỉ set khi Draft.
        public List<CriterionItem>? Criteria { get; set; }

        // EVA1-B5 / HĐ-2 — 3 luật lọc CỨNG cho sàng CV (D19: lá chắn chi phí Gemini số 1 — hard-filter
        // TRƯỚC AI). Cột đã có (Models/Campaign.cs); mục rỗng/trắng bị loại lặng.
        //   requiredSkills:      CV phải có ĐỦ mọi mục
        //   keywordsAny:         CV phải có ÍT NHẤT 1 mục
        //   minYearsExperience:  số năm tối thiểu; ∈ [0, 60]; 0 = không ràng buộc (KHÔNG cần sentinel "clear")
        public List<string>? RequiredSkills { get; set; }
        public List<string>? KeywordsAny { get; set; }
        public int? MinYearsExperience { get; set; }

        [Required]
        public DateTime? StartsAt { get; set; }

        [Required]
        public DateTime? ExpiresAt { get; set; }

        public List<QuestionItem> Questions { get; set; } = new();
    }

    public class UploadCampaignFilesRequest
    {
        public IFormFile? JdFile { get; set; }
        public IFormFile? CriteriaFile { get; set; }
    }

    public class UpdateCampaignRequest
    {
        public string Title { get; set; }
        public string? Language { get; set; }
        public string? Seniority { get; set; }

        public string? Domain { get; set; }

        public int? MaxCandidates { get; set; }

        public int? TimeLimitMinutes { get; set; }

        public bool? AntiCheatEnabled { get; set; }

        // SEC-1: bật/tắt face-verify — null = không đổi (giữ giá trị cũ), như AntiCheatEnabled (C3).
        public bool? FaceVerifyEnabled { get; set; }

        // E5: ngưỡng % pass/fail (0–100). null = không đổi (giữ giá trị cũ).
        public int? PassScorePct { get; set; }

        // INT-17: bật/tắt phỏng vấn thích ứng + trần câu — null = không đổi (giữ cũ), như AntiCheatEnabled.
        public bool? AdaptiveEnabled { get; set; }
        public bool? GroundingEnabled { get; set; }
        // null = KHÔNG ĐỔI (giữ giá trị cũ), đồng nếp với các trần khác ở DTO này.
        // ⚠ Hệ quả: đã đặt trần thì không gỡ về null được qua API — muốn "bỏ trần" thì đặt một
        // số lớn hơn số ứng viên của chiến dịch. Đánh đổi có chủ ý để không lệch nếp các field kia.
        public int? MaxConcurrentInterviews { get; set; }
        public int? MaxFollowUps { get; set; }
        public int? MaxQuestions { get; set; }
        public int? MaxDeepPerQuestion { get; set; }   // INT-17b
        // NGÂN HÀNG ĐỀ — null = KHÔNG ĐỔI (cùng nếp các trần trên). Muốn quay về "thi hết bộ" thì
        // đặt một số ≥ số câu của chiến dịch; API không gỡ được về null, đúng đánh đổi đã chọn ở trên.
        public int? QuestionsPerSession { get; set; }

        // C11: cập nhật/ghi đè JD & Criteria dạng TEXT trực tiếp (text ưu tiên file → xoá *_file_url).
        public string? JdText { get; set; }
        public string? CriteriaText { get; set; }

        // C12: ghi đè tiêu chí structured (replace-all atomic) — chỉ khi Draft, ngược lại 409.
        public List<CriterionItem>? Criteria { get; set; }

        // EVA1-B5 / HĐ-2 — 3 luật lọc CỨNG cho sàng CV. LỚP TÁCH RỜI với CreateCampaignRequest (không
        // kế thừa) — thiếu ở đây thì mỗi lần HR bấm Lưu là âm thầm xoá cấu hình lọc.
        //   null / vắng  = KHÔNG ĐỔI
        //   []           = XOÁ luật
        //   minYearsExperience: 0 = XOÁ luật ("tối thiểu 0 năm" = không ràng buộc), KHÔNG phải sentinel bẩn.
        // Cửa trạng thái: Draft, HOẶC Active khi campaign chưa có ứng viên nào; Closed/Archived → 409.
        public List<string>? RequiredSkills { get; set; }
        public List<string>? KeywordsAny { get; set; }
        public int? MinYearsExperience { get; set; }

        public DateTime? StartsAt { get; set; }

        public DateTime? ExpiresAt { get; set; }
    }

    public class TransitionStatusRequest
    {
        public CampaignStatus Status { get; set; }   // Active→Closed→Archived (Draft→Active dùng /publish)
    }

    public class CampaignQuestionResponse
    {
        public Guid Id { get; set; }
        public string QuestionText { get; set; }
        public string Source { get; set; }
        public bool IsRequired { get; set; }

        // R10 — có giá trị = câu do AI sinh mà HR đã sửa nội dung ⇒ lượt "sinh lại" GIỮ nó, không thay.
        // Additive (FE cũ bỏ qua field lạ). FE cần field này để hộp thoại xác nhận đếm đúng: nó đang
        // đếm theo `source` nên vẫn xếp câu AI-đã-chỉnh vào nhóm "sẽ bị THAY" — hiện là dương tính giả.
        public DateTime? HrEditedAt { get; set; }

        // Đáp án mẫu HR soạn. null = chưa soạn HOẶC đây là danh sách campaign (list bỏ field này để
        // payload không cõng 200 × 5.000 ký tự mỗi lần mở trang) — xem FromEntity(includeSampleAnswer).
        public string? SampleAnswer { get; set; }

        // Nhóm chủ đề (ngân hàng đề). null = chưa phân nhóm.
        public string? QuestionGroup { get; set; }
    }

    // CAMP-16 — một mốc điểm khi ĐỌC.
    public class CriterionLevelResponse
    {
        public int Score { get; set; }
        public string Descriptor { get; set; } = null!;
    }

    // C12: tiêu chí có cấu trúc trả về (đọc/duyệt). order_no + source (HrEdited/AiSuggested).
    public class CampaignCriterionResponse
    {
        public Guid Id { get; set; }
        public int OrderNo { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }
        public int MaxScore { get; set; }
        // RNK1 · HĐ-5 — điểm sàn %. null = không sàn. FE echo lại field này ở PUT (cùng với Id) khi sửa.
        public int? MinPct { get; set; }
        public string Source { get; set; } = null!;

        /// <summary>
        /// CAMP-16 — mốc điểm, sắp tăng dần theo <c>score</c>. Rỗng = CHƯA khai mốc (Interview dùng dải
        /// mặc định). Field này được nạp ở MỌI đường trả tiêu chí, kể cả danh sách campaign: trả mảng
        /// rỗng vì "chưa nạp" là nói dối "chưa khai mốc" — cùng lớp lỗi với `roadmaps` list từng trả
        /// `milestones: []`.
        /// </summary>
        public List<CriterionLevelResponse> Levels { get; set; } = new();
    }

    /// <summary>HR technical screener bước 1 — 1 nhu cầu công việc (đọc).</summary>
    public class JobNeedResponse
    {
        public string NeedId { get; set; } = null!;
        public string Category { get; set; } = null!;   // Technical | WorkStyle | Communication | Growth
        public string Text { get; set; } = null!;
        public string Source { get; set; } = null!;     // AiSuggested | HrEdited — server sở hữu (F10)
        /// <summary>RNK1 · HĐ-6 — nhu cầu bắt buộc: thiếu bằng chứng Strong/Partial ⇒ ứng viên
        /// bị loại (<c>eligible = false</c>) ngay lúc sàng. HR sở hữu; AI không đề xuất.</summary>
        public bool IsMustHave { get; set; }
    }

    /// <summary>
    /// HR sửa nhu cầu công việc (replace-all). <c>Source</c> KHÔNG có ở đây là CỐ Ý: nguồn gốc là
    /// sự thật do server sở hữu — cho client khai thì HR tự dán nhãn "AI đề xuất" cho dòng mình gõ
    /// tay, đúng lỗ F10 đã bịt cho <c>campaign_questions.source</c>.
    /// </summary>
    public class JobNeedInput
    {
        /// <summary>Echo lại id đang có để kết quả sàng đã lưu còn trỏ đúng dòng; trống ⇒ cấp mới.</summary>
        public string? NeedId { get; set; }
        public string? Category { get; set; }
        public string? Text { get; set; }
        /// <summary>
        /// RNK1 · HĐ-6 — nhu cầu bắt buộc (điều kiện loại). CÓ ở đây (khác <c>Source</c>): là quyết
        /// định nghiệp vụ của HR, không phải nhãn nguồn gốc ⇒ giá trị client GIỮ NGUYÊN. null ⇒ false.
        /// </summary>
        public bool? IsMustHave { get; set; }
    }

    /// <summary>RNK1 · HĐ-8 — một nhóm chủ đề trong ngân hàng đề + số câu thuộc nhóm đó.</summary>
    public class QuestionBankGroup
    {
        public string Name { get; set; } = null!;
        public int Count { get; set; }
    }

    /// <summary>
    /// RNK1 · HĐ-8 — tóm tắt NGÂN HÀNG ĐỀ, tính READ-TIME trên mọi <see cref="CampaignResponse"/>.
    /// <c>Warnings</c> KHÔNG rỗng ⇒ publish trả <b>400</b> <c>{ code: "QUESTION_BANK_INVALID", warnings }</c>.
    /// </summary>
    public class QuestionBankSummary
    {
        /// <summary>Tổng số câu trong bộ.</summary>
        public int Total { get; set; }
        /// <summary>Số câu <c>isRequired</c> — MỌI ứng viên đều gặp (selector giữ hết, kể cả khi vượt K).</summary>
        public int AlwaysAsked { get; set; }
        /// <summary>K = số câu mỗi buổi. null = thi trọn bộ.</summary>
        public int? QuestionsPerSession { get; set; }
        /// <summary>Phân bố theo nhóm. Nhóm null/"" gộp thành <c>"Chung"</c>, gộp không phân biệt hoa/thường (như selector).</summary>
        public List<QuestionBankGroup> Groups { get; set; } = new();
        /// <summary>Ca bất thường (đọc được cho HR + là gate publish). Rỗng = ổn.</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// NGUỒN DUY NHẤT: dùng chung cho <see cref="CampaignResponse.FromEntity"/> (read-time) và
        /// gate publish (đọc <see cref="Warnings"/>).
        /// </summary>
        public static QuestionBankSummary Build(
            IEnumerable<CampaignQuestion> questionsSource,
            int? questionsPerSession, int? maxDeepPerQuestion, int? maxQuestions)
        {
            // Sắp theo (CreatedAt, Id) TRƯỚC — như `FromEntity` sắp `Questions` — để:
            //   • casing hiển thị của nhóm = casing HR gõ ở câu SỚM NHẤT của nhóm đó (tất định);
            //   • thứ tự nhóm ổn định giữa các lần gọi (không phụ thuộc thứ tự EF nạp).
            var questions = (questionsSource as IEnumerable<CampaignQuestion> ?? Array.Empty<CampaignQuestion>())
                .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id)
                .ToList();
            var total = questions.Count;
            var alwaysAsked = questions.Count(q => q.IsRequired);

            // Nhóm: normalize null/whitespace → null; gộp OrdinalIgnoreCase (như QuestionPoolSelector).
            // Thứ tự: nhóm "Chung" (null) trước, rồi theo tên hiển thị.
            var groups = questions
                .GroupBy(
                    q => string.IsNullOrWhiteSpace(q.QuestionGroup) ? null : q.QuestionGroup!.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    IsGeneral = g.Key is null,
                    // g giữ thứ tự nguồn (đã sắp CreatedAt,Id) ⇒ First() = casing của câu sớm nhất.
                    Name = g.Key is null ? "Chung" : g.First().QuestionGroup!.Trim(),
                    Count = g.Count(),
                })
                .OrderBy(x => x.IsGeneral ? 0 : 1)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new QuestionBankGroup { Name = x.Name, Count = x.Count })
                .ToList();

            var k = questionsPerSession ?? total;
            var d = maxDeepPerQuestion ?? 0;
            var t = maxQuestions ?? 0;

            var warnings = new List<string>();
            // (1) K > total: không phải lỗi ở save (FE gửi PUT campaign trước questions) — selector rơi
            //     về "thi trọn bộ". Nhưng publish thì phải sạch.
            if (questionsPerSession is int kSet && kSet > total)
                warnings.Add(
                    $"questions_per_session ({kSet}) lớn hơn số câu trong bộ ({total}) — ứng viên sẽ thi trọn bộ.");
            // (2) alwaysAsked > K: selector giữ HẾT câu bắt buộc ⇒ buổi dài hơn K, không còn khe cho câu rút.
            if (alwaysAsked > k)
                warnings.Add(
                    $"Số câu bắt buộc ({alwaysAsked}) nhiều hơn số câu mỗi buổi ({k}) — mỗi buổi sẽ dài hơn {k} câu.");
            // (3) K×(1+d) > T — dùng chung luật với RNK1-B6 (AdaptiveBudgetRule).
            if (Isas.CampaignService.Validation.AdaptiveBudgetRule.Check(k, d, t) is { } v)
                warnings.Add(
                    $"Ngân sách buổi ({v.Have}) không đủ cho {v.Questions} câu × (1 + {v.Deep} đào sâu) = {v.Need} câu.");

            return new QuestionBankSummary
            {
                Total = total,
                AlwaysAsked = alwaysAsked,
                QuestionsPerSession = questionsPerSession,
                Groups = groups,
                Warnings = warnings,
            };
        }
    }

    public class CampaignResponse
    {
        public Guid Id { get; set; }
        public Guid OrgId { get; set; }   // BK4: owner = ORG (AUTH-8)
        public string Title { get; set; }
        public string? Domain { get; set; }
        public string Language { get; set; } = "vi";
        public string Seniority { get; set; } = "Junior";
        public string Status { get; set; }
        public int? MaxCandidates { get; set; }
        public int? TimeLimitMinutes { get; set; }
        public bool AntiCheatEnabled { get; set; }
        public bool FaceVerifyEnabled { get; set; }   // SEC-1: bật face-verify (B2B-only)
        public int? PassScorePct { get; set; }   // E5: ngưỡng % pass/fail (null = HR quyết tay)
        // RNK1 · HĐ-2 / CAMP-21 — luật câu bỏ trống tính 0 điểm. SERVER SỞ HỮU (không có trên
        // Create/Update request): campaign mới = true, campaign trước RNK1 = false. FE chỉ hiển thị.
        public bool SkipPenalty { get; set; }
        // SCP1-B13 — con trỏ chính sách chấm ĐANG ÁP (khớp campaigns.{interview,cv}_policy_version).
        // null = chưa áp chính sách nào ⇒ điểm bằng công thức mặc định. CHỈ số version — nội dung biểu
        // thức xem qua endpoint danh sách chính sách (đã có kiểm soát quyền). KHÔNG lộ cho ứng viên
        // (CAMP-15): DTO ứng viên KHÔNG mang trường này.
        public int? InterviewPolicyVersion { get; set; }
        public int? CvPolicyVersion { get; set; }
        public bool AdaptiveEnabled { get; set; }   // INT-17: phỏng vấn thích ứng (B2B opt-in)
        public bool GroundingEnabled { get; set; }  // T8: grounding snapshot (B2B opt-in)
        public int? MaxConcurrentInterviews { get; set; }   // trần thi đồng thời (null = không giới hạn)
        public int? MaxFollowUps { get; set; }      // INT-17: trần câu thích ứng (null = mặc định Interview)
        public int? MaxQuestions { get; set; }      // INT-17: trần tổng câu (null = mặc định Interview)
        public int? MaxDeepPerQuestion { get; set; }   // INT-17b: trần đào sâu mỗi câu (null/0 = chế độ cũ)
        // NGÂN HÀNG ĐỀ: số câu mỗi ứng viên thi (null = thi HẾT bộ câu hỏi, hành vi cũ).
        public int? QuestionsPerSession { get; set; }
        // CAMP-18 — định danh bộ thước đo đang hiệu lực + ai/lúc nào đổi. FE hiện chip "Thước đo v2"
        // kèm tooltip; ứng viên đã chấm bằng bản cũ giữ nguyên điểm.
        public int RubricVersion { get; set; } = 1;
        public DateTime? RubricVersionUpdatedAt { get; set; }
        public Guid? RubricVersionUpdatedBy { get; set; }

        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<CampaignQuestionResponse> Questions { get; set; }
        // RNK1 · HĐ-8 — tóm tắt ngân hàng đề (total / alwaysAsked / K / groups / warnings), tính read-time.
        public QuestionBankSummary QuestionBank { get; set; } = new();
        public List<CampaignCriterionResponse> Criteria { get; set; }   // C12: tiêu chí structured
        // HR technical screener bước 1 — thước đo dùng cho MỌI CV của campaign này. `[]` khi chưa
        // chốt (chưa publish hoặc AI không suy được từ JD) ⇒ sàng CV chưa chạy được.
        public List<JobNeedResponse> JobNeeds { get; set; } = new();
        // EVA1-B5 / HĐ-2 — 3 luật lọc CỨNG sàng CV (đọc lại đúng kiểu đã ghi). null = không áp luật đó.
        public List<string>? RequiredSkills { get; set; }
        public List<string>? KeywordsAny { get; set; }
        public int? MinYearsExperience { get; set; }
        public string? JDText { get; set; }
        public string? CriteriaText { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <param name="includeSampleAnswer">
        /// <c>false</c> cho DANH SÁCH campaign: `GetCampaignsAsync` cũng `.Include(Questions)` và dùng
        /// chung mapper này, nên trả đáp án mẫu ở đó là mỗi thẻ campaign cõng thêm tới 200 × 5.000 ký tự.
        /// Màn danh sách không hiển thị đáp án — chỉ màn chi tiết/sửa mới cần.
        /// </param>
        public static CampaignResponse FromEntity(Campaign c, bool includeSampleAnswer = true) => new CampaignResponse
        {
            Id = c.Id,
            OrgId = c.OrgId,
            Title = c.Title,
            Domain = c.Domain,
            Language = c.Language,
            Seniority = c.Seniority,
            Status = c.Status.ToString(),
            MaxCandidates = c.MaxCandidates,
            TimeLimitMinutes = c.TimeLimitMinutes,
            AntiCheatEnabled = c.AntiCheatEnabled,
            FaceVerifyEnabled = c.FaceVerifyEnabled,
            PassScorePct = c.PassScorePct,
            SkipPenalty = c.SkipPenalty,                         // RNK1 · HĐ-2 / CAMP-21
            InterviewPolicyVersion = c.InterviewPolicyVersion,   // SCP1-B13
            CvPolicyVersion = c.CvPolicyVersion,                 // SCP1-B13
            AdaptiveEnabled = c.AdaptiveEnabled,   // INT-17
            GroundingEnabled = c.GroundingEnabled,
            MaxConcurrentInterviews = c.MaxConcurrentInterviews,
            MaxFollowUps = c.MaxFollowUps,
            MaxQuestions = c.MaxQuestions,
            MaxDeepPerQuestion = c.MaxDeepPerQuestion,   // INT-17b
            QuestionsPerSession = c.QuestionsPerSession,
            RubricVersion = c.RubricVersion,                       // CAMP-18
            RubricVersionUpdatedAt = c.RubricVersionUpdatedAt,
            RubricVersionUpdatedBy = c.RubricVersionUpdatedBy,
            StartsAt = c.StartsAt,
            ExpiresAt = c.ExpiresAt,
            JobNeeds = (c.JobNeeds ?? new List<JobNeed>())
                .Select(n => new JobNeedResponse
                {
                    NeedId = n.NeedId,
                    Category = n.Category,
                    Text = n.Text,
                    Source = n.Source,
                    IsMustHave = n.IsMustHave,   // RNK1 · HĐ-6
                }).ToList(),
            // F10: sắp theo ĐÚNG thứ tự ứng viên sẽ gặp (ParticipationService dùng CreatedAt, Id) —
            // FE echo `id` lại khi PUT, nên thứ tự response phải ổn định giữa các lần gọi.
            Questions = c.Questions
                .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id)
                .Select(q => new CampaignQuestionResponse
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                Source = q.Source.ToString(),
                IsRequired = q.IsRequired,
                HrEditedAt = q.HrEditedAt,   // R10
                SampleAnswer = includeSampleAnswer ? q.SampleAnswer : null,
                QuestionGroup = q.QuestionGroup
            }).ToList(),
            // RNK1 · HĐ-8 — tóm tắt ngân hàng đề (đọc từ CÙNG c.Questions đã nạp, không query thêm).
            QuestionBank = QuestionBankSummary.Build(
                c.Questions, c.QuestionsPerSession, c.MaxDeepPerQuestion, c.MaxQuestions),
            Criteria = c.Criteria
                .OrderBy(cr => cr.OrderNo)
                .Select(cr => new CampaignCriterionResponse
                {
                    Id = cr.Id,
                    OrderNo = cr.OrderNo,
                    Name = cr.Name,
                    Description = cr.Description,
                    Weight = cr.Weight,
                    MaxScore = cr.MaxScore,
                    MinPct = cr.MinPct,                 // RNK1 · HĐ-5
                    Source = cr.Source.ToString(),
                    Levels = (cr.Levels ?? new List<CampaignCriterionLevel>())
                        .OrderBy(l => l.Score)
                        .Select(l => new CriterionLevelResponse { Score = l.Score, Descriptor = l.Descriptor })
                        .ToList()
                }).ToList(),
            RequiredSkills = c.RequiredSkills,        // EVA1-B5 — luật lọc cứng sàng CV
            KeywordsAny = c.KeywordsAny,
            MinYearsExperience = c.MinYearsExperience,
            JDText = c.JDText,
            CriteriaText = c.CriteriaText,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        };
    }
}
