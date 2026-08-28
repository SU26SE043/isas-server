using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Data;

/// <summary>
/// TOP1-B2 — danh mục chủ đề luyện tập B2C: 3 nghề (BA/BE/FE) × 4 cấp độ (Fresher/Junior/Middle/
/// Senior) × 8 chủ đề × 2 ngôn ngữ (vi/en) = 192 dòng.
///
/// NGUỒN NỘI DUNG: đọc TAY (không tách máy, không dịch máy) từ prose
/// <c>src/services/Isas.AIService/app/seniority.py::_KNOWLEDGE_DEFAULTS</c> — prose đó trả lời "hỏi
/// SÂU tới đâu" cho mỗi (nghề, cấp độ); bảng này trả lời câu hỏi khác: "hỏi CÁI GÌ". KHÔNG sửa
/// <c>_KNOWLEDGE_DEFAULTS</c> — nó đang bị test khoá bằng substring.
///
/// Mỗi chủ đề gắn <see cref="PracticeTopic.CriterionName"/> = tên MỘT tiêu chí
/// <see cref="ScoringScope.WhenTargeted"/> của đúng (nghề, ngôn ngữ) trong
/// <see cref="B2CRubricSeed"/> — bị khoá bằng <c>PracticeTopicSeedTests</c>.
///
/// IDEMPOTENT: GUID CỐ ĐỊNH (mẫu <see cref="B2CRubricSeed"/>) dựng từ (nghề, cấp độ, chỉ số 1-8);
/// bản <c>en</c> suy từ bản <c>vi</c> bằng đúng phép biến đổi <see cref="B2CRubricSeed"/> dùng
/// (bytes[0] ^= 0x11) để không đụng độ.
///
/// Cách giao seed: <c>HasData</c> ở <c>InterviewDbContext</c> (chỉ Npgsql, gate
/// <c>Database.IsNpgsql()</c>) → EF sinh <c>InsertData</c> literal trong migration.
/// </summary>
public static class PracticeTopicSeed
{
    public const int SeedVersion = 1;

    // ── Ánh xạ tên tiêu chí NỘI DUNG (WhenTargeted) sang bản dịch tiếng Anh của chính nó ─────────
    // Khớp NGUYÊN VĂN B2CRubricSeed.EnglishName (private, không gọi được từ đây) — 6 tên WhenTargeted
    // dùng trong 3 nghề. "Tư duy giải quyết vấn đề"/"Giải quyết vấn đề & thuật toán"/"Giải quyết vấn
    // đề" đều dịch thành "Problem solving" (đúng như B2CRubricSeed — 3 nghề chia sẻ một tên tiếng Anh).
    private static readonly Dictionary<string, string> CriterionNameToEnglish = new()
    {
        ["Phân tích yêu cầu"] = "Requirements analysis",
        ["Hiểu nghiệp vụ & các bên liên quan"] = "Business domain & stakeholders",
        ["Tư duy giải quyết vấn đề"] = "Problem solving",
        ["Chiều sâu kỹ thuật"] = "Technical depth",
        ["Thiết kế hệ thống & CSDL"] = "System design & databases",
        ["Giải quyết vấn đề & thuật toán"] = "Problem solving",
        ["Giải quyết vấn đề"] = "Problem solving",
        ["Ý thức UI/UX & accessibility"] = "UI/UX & accessibility awareness",
    };

    private static readonly (int Level, string Seniority)[] Levels =
    [
        (0, "Fresher"),
        (1, "Junior"),
        (2, "Middle"),
        (3, "Senior"),
    ];

    public static List<PracticeTopic> Build()
    {
        var all = new List<PracticeTopic>();

        all.AddRange(Cell(JobCategory.BA, "ba", Levels[0], BaFresher));
        all.AddRange(Cell(JobCategory.BA, "ba", Levels[1], BaJunior));
        all.AddRange(Cell(JobCategory.BA, "ba", Levels[2], BaMiddle));
        all.AddRange(Cell(JobCategory.BA, "ba", Levels[3], BaSenior));

        all.AddRange(Cell(JobCategory.BE, "be", Levels[0], BeFresher));
        all.AddRange(Cell(JobCategory.BE, "be", Levels[1], BeJunior));
        all.AddRange(Cell(JobCategory.BE, "be", Levels[2], BeMiddle));
        all.AddRange(Cell(JobCategory.BE, "be", Levels[3], BeSenior));

        all.AddRange(Cell(JobCategory.FE, "fe", Levels[0], FeFresher));
        all.AddRange(Cell(JobCategory.FE, "fe", Levels[1], FeJunior));
        all.AddRange(Cell(JobCategory.FE, "fe", Levels[2], FeMiddle));
        all.AddRange(Cell(JobCategory.FE, "fe", Levels[3], FeSenior));

        return all;
    }

    private static Guid EnglishId(Guid vietnameseId)
    {
        var bytes = vietnameseId.ToByteArray();
        bytes[0] ^= 0x11; // cùng phép biến đổi B2CRubricSeed.EnglishId — cố định, không đụng độ
        return new Guid(bytes);
    }

    private static Guid TopicId(string jobCode, int level, int index) =>
        new($"1{jobCode}0{level}000-0000-0000-0000-{index:D12}");

    // Sinh CẢ HAI row (vi + en) cùng lúc cho mỗi chủ đề — tránh side-channel qua state tĩnh
    // (Build() gọi lại nhiều lần vẫn cho đúng cùng một tập, không phụ thuộc thứ tự enumerate).
    private static IEnumerable<PracticeTopic> Cell(
        JobCategory job, string jobCode, (int Level, string Seniority) level, TopicRow[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var viId = TopicId(jobCode, level.Level, i + 1);
            var topicKey = $"top1.{jobCode}.{level.Seniority.ToLowerInvariant()}.{row.KeySuffix}";

            yield return new PracticeTopic
            {
                Id = viId,
                TopicKey = topicKey,
                JobCategory = job,
                Seniority = level.Seniority,
                Language = "vi",
                Label = row.LabelVi,
                CriterionName = row.CriterionVi,
                DisplayOrder = i + 1,
                IsActive = true,
                Version = SeedVersion,
            };

            yield return new PracticeTopic
            {
                Id = EnglishId(viId),
                TopicKey = topicKey,
                JobCategory = job,
                Seniority = level.Seniority,
                Language = "en",
                Label = row.LabelEn,
                CriterionName = CriterionNameToEnglish[row.CriterionVi],
                DisplayOrder = i + 1,
                IsActive = true,
                Version = SeedVersion,
            };
        }
    }

    private readonly record struct TopicRow(
        string KeySuffix, string LabelVi, string LabelEn, string CriterionVi);

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BA — Business Analyst. 3 tiêu chí WhenTargeted: "Phân tích yêu cầu" · "Hiểu nghiệp vụ & các
    // bên liên quan" · "Tư duy giải quyết vấn đề".
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private static readonly TopicRow[] BaFresher =
    [
        new("user-story-basics",
            "User story: cấu trúc và cách viết cơ bản",
            "User stories: structure and how to write one",
            "Phân tích yêu cầu"),
        new("use-case-basics",
            "Use case: xác định tác nhân (actor) và luồng chính",
            "Use cases: identifying the actor and the main flow",
            "Phân tích yêu cầu"),
        new("srs-purpose",
            "Tài liệu đặc tả yêu cầu (SRS): mục đích và nội dung chính",
            "Software Requirements Specification (SRS): purpose and typical content",
            "Phân tích yêu cầu"),
        new("functional-vs-nonfunctional",
            "Phân biệt yêu cầu chức năng và phi chức năng qua ví dụ",
            "Telling functional and non-functional requirements apart, with examples",
            "Phân tích yêu cầu"),
        new("read-single-requirement",
            "Đọc và diễn giải lại một yêu cầu cụ thể bằng lời của mình",
            "Reading a single requirement and restating it in your own words",
            "Phân tích yêu cầu"),
        new("clarifying-question",
            "Đặt câu hỏi làm rõ khi một yêu cầu chưa rõ ràng",
            "Asking a clarifying question when a requirement is unclear",
            "Tư duy giải quyết vấn đề"),
        new("single-stakeholder-check",
            "Trao đổi với một stakeholder để xác nhận hiểu đúng yêu cầu",
            "Talking with one stakeholder to confirm you understood a requirement correctly",
            "Hiểu nghiệp vụ & các bên liên quan"),
        new("ba-role-basics",
            "Vai trò và trách nhiệm cơ bản của BA trong dự án",
            "A BA's basic role and responsibilities on a project",
            "Hiểu nghiệp vụ & các bên liên quan"),
    ];

    private static readonly TopicRow[] BaJunior =
    [
        new("write-user-story-full-feature",
            "Tự viết user story/use case hoàn chỉnh cho một tính năng",
            "Writing a complete user story or use case for one feature on your own",
            "Phân tích yêu cầu"),
        new("workshop-few-stakeholders",
            "Chạy workshop thu thập yêu cầu với 1-2 stakeholder",
            "Running a requirements-gathering workshop with one or two stakeholders",
            "Hiểu nghiệp vụ & các bên liên quan"),
        new("acceptance-criteria",
            "Viết acceptance criteria rõ ràng cho một tính năng",
            "Writing clear acceptance criteria for a feature",
            "Phân tích yêu cầu"),
        new("spot-ambiguous-requirement",
            "Phát hiện yêu cầu mơ hồ hoặc thiếu sót",
            "Spotting a requirement that is ambiguous or incomplete",
            "Phân tích yêu cầu"),
        new("ask-follow-up-right-spot",
            "Hỏi lại đúng chỗ khi phát hiện vấn đề trong yêu cầu",
            "Asking the right follow-up question when something in a requirement looks off",
            "Tư duy giải quyết vấn đề"),
        new("client-changes-mind",
            "Xử lý tình huống khách hàng đổi ý giữa chừng",
            "Handling a situation where the client changes their mind midway through",
            "Tư duy giải quyết vấn đề"),
        new("conflicting-department-requirements",
            "Xử lý yêu cầu chồng chéo giữa hai bộ phận",
            "Handling requirements that conflict between two departments",
            "Tư duy giải quyết vấn đề"),
        new("small-workshop-facilitation",
            "Làm việc trực tiếp với 1-2 stakeholder trong một buổi workshop",
            "Working directly with one or two stakeholders during a small workshop",
            "Hiểu nghiệp vụ & các bên liên quan"),
    ];

    private static readonly TopicRow[] BaMiddle =
    [
        new("workshop-conflicting-stakeholders",
            "Chủ trì workshop với nhiều stakeholder có quan điểm mâu thuẫn",
            "Facilitating a workshop with multiple stakeholders who disagree",
            "Hiểu nghiệp vụ & các bên liên quan"),
        new("process-mapping",
            "Vẽ quy trình nghiệp vụ (process mapping) cho một luồng công việc",
            "Mapping a business process for one workflow",
            "Phân tích yêu cầu"),
        new("scope-vs-deadline",
            "Phân tích đánh đổi giữa phạm vi và thời hạn dự án",
            "Weighing the trade-off between project scope and deadline",
            "Tư duy giải quyết vấn đề"),
        new("build-vs-buy",
            "So sánh phương án tự xây dựng và mua giải pháp có sẵn",
            "Comparing building a solution in-house versus buying one",
            "Tư duy giải quyết vấn đề"),
        new("change-impact-analysis",
            "Đánh giá tác động khi yêu cầu thay đổi giữa dự án",
            "Assessing the impact when a requirement changes mid-project",
            "Phân tích yêu cầu"),
        new("stakeholder-conflict-of-interest",
            "Xử lý xung đột lợi ích giữa các bên liên quan",
            "Handling a conflict of interest between stakeholders",
            "Hiểu nghiệp vụ & các bên liên quan"),
        new("backlog-prioritization",
            "Ưu tiên hoá backlog theo giá trị nghiệp vụ",
            "Prioritizing the backlog by business value",
            "Tư duy giải quyết vấn đề"),
        new("facilitate-complex-meeting",
            "Dẫn dắt một cuộc họp yêu cầu phức tạp",
            "Leading a complex requirements meeting",
            "Hiểu nghiệp vụ & các bên liên quan"),
    ];

    private static readonly TopicRow[] BaSenior =
    [
        new("shape-business-area-solution",
            "Định hình giải pháp cho cả một mảng nghiệp vụ",
            "Shaping the solution direction for an entire business area",
            "Phân tích yêu cầu"),
        new("balance-tech-budget-politics",
            "Cân bằng ràng buộc kỹ thuật, ngân sách và chính trị nội bộ",
            "Balancing technical, budget, and internal-politics constraints",
            "Tư duy giải quyết vấn đề"),
        new("mentor-junior-ba",
            "Dẫn dắt và kèm cặp BA/PO junior",
            "Mentoring and guiding junior BAs or POs",
            "Hiểu nghiệp vụ & các bên liên quan"),
        new("requirement-quality-multi-project",
            "Chịu trách nhiệm chất lượng yêu cầu ở quy mô nhiều dự án",
            "Owning requirement quality across multiple projects",
            "Phân tích yêu cầu"),
        new("decide-with-incomplete-info",
            "Ra quyết định khi thiếu thông tin đầy đủ",
            "Making a decision when information is incomplete",
            "Tư duy giải quyết vấn đề"),
        new("persuade-senior-stakeholder",
            "Thuyết phục stakeholder cấp cao",
            "Persuading a senior stakeholder",
            "Hiểu nghiệp vụ & các bên liên quan"),
        new("measure-value-after-rollout",
            "Đo lường giá trị nghiệp vụ sau khi triển khai",
            "Measuring business value after a solution goes live",
            "Tư duy giải quyết vấn đề"),
        new("manage-requirement-risk-at-scale",
            "Quản lý rủi ro chất lượng yêu cầu ở quy mô lớn",
            "Managing requirement-quality risk at scale",
            "Phân tích yêu cầu"),
    ];

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BE — Backend. 3 tiêu chí WhenTargeted: "Chiều sâu kỹ thuật" · "Thiết kế hệ thống & CSDL" ·
    // "Giải quyết vấn đề & thuật toán".
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private static readonly TopicRow[] BeFresher =
    [
        new("array-list-basics",
            "Cấu trúc dữ liệu mảng và list: khi nào dùng cái nào",
            "Arrays vs. lists: when to use which",
            "Chiều sâu kỹ thuật"),
        new("hash-map-basics",
            "Hash map: khái niệm và tình huống áp dụng",
            "Hash maps: the concept and when to use one",
            "Chiều sâu kỹ thuật"),
        new("simple-crud-api",
            "Viết một API CRUD đơn giản cho một tài nguyên",
            "Writing a simple CRUD API for one resource",
            "Chiều sâu kỹ thuật"),
        new("sql-select-basics",
            "Câu lệnh SQL SELECT cơ bản để lấy dữ liệu",
            "Basic SQL SELECT to fetch data",
            "Thiết kế hệ thống & CSDL"),
        new("sql-insert-update-basics",
            "Câu lệnh SQL INSERT và UPDATE cơ bản",
            "Basic SQL INSERT and UPDATE statements",
            "Thiết kế hệ thống & CSDL"),
        new("simple-join",
            "Viết một câu JOIN đơn giản giữa hai bảng",
            "Writing a simple JOIN across two tables",
            "Thiết kế hệ thống & CSDL"),
        new("http-method-choice",
            "Phân biệt và chọn đúng HTTP method cho một thao tác",
            "Telling HTTP methods apart and picking the right one for an action",
            "Giải quyết vấn đề & thuật toán"),
        new("http-method-why",
            "Vì sao một thao tác API nên dùng đúng HTTP method",
            "Why an API action should use the correct HTTP method",
            "Giải quyết vấn đề & thuật toán"),
    ];

    private static readonly TopicRow[] BeJunior =
    [
        new("validate-input",
            "Validate input đầu vào cho một API",
            "Validating input for an API",
            "Chiều sâu kỹ thuật"),
        new("error-handling-status-code",
            "Xử lý lỗi và trả đúng status code",
            "Handling errors and returning the right status code",
            "Chiều sâu kỹ thuật"),
        new("full-feature-api",
            "Viết một API hoàn chỉnh cho một tính năng cụ thể",
            "Writing a complete API for one feature",
            "Chiều sâu kỹ thuật"),
        new("join-group-by",
            "Viết truy vấn có JOIN và GROUP BY",
            "Writing a query with JOIN and GROUP BY",
            "Thiết kế hệ thống & CSDL"),
        new("index-basics",
            "Cơ chế index cơ bản: khi nào một truy vấn cần index",
            "Basic indexing: when a query needs an index",
            "Thiết kế hệ thống & CSDL"),
        new("slow-query-missing-index",
            "Chẩn đoán vì sao một truy vấn chạy chậm do thiếu index",
            "Diagnosing why a query is slow because of a missing index",
            "Thiết kế hệ thống & CSDL"),
        new("debug-runtime-error",
            "Debug một lỗi runtime thường gặp",
            "Debugging a common runtime error",
            "Giải quyết vấn đề & thuật toán"),
        new("wrong-data-missing-filter",
            "Chẩn đoán vì sao API trả sai dữ liệu do thiếu điều kiện lọc",
            "Diagnosing why an API returns wrong data because of a missing filter condition",
            "Giải quyết vấn đề & thuật toán"),
    ];

    private static readonly TopicRow[] BeMiddle =
    [
        new("module-schema-design",
            "Thiết kế schema database cho một module cụ thể",
            "Designing a database schema for one module",
            "Thiết kế hệ thống & CSDL"),
        new("storage-caching-choice",
            "Chọn giữa các phương án lưu trữ dữ liệu và caching",
            "Choosing between storage and caching approaches",
            "Thiết kế hệ thống & CSDL"),
        new("optimize-slow-query",
            "Tối ưu một truy vấn đang chạy chậm",
            "Optimizing a slow-running query",
            "Thiết kế hệ thống & CSDL"),
        new("race-condition",
            "Xử lý race condition trong hệ thống đồng thời",
            "Handling a race condition in a concurrent system",
            "Chiều sâu kỹ thuật"),
        new("deadlock",
            "Xử lý deadlock giữa các giao dịch",
            "Handling a deadlock between transactions",
            "Chiều sâu kỹ thuật"),
        new("test-complex-logic",
            "Viết test cho logic nghiệp vụ phức tạp",
            "Writing tests for complex business logic",
            "Chiều sâu kỹ thuật"),
        new("consistency-vs-performance",
            "Đánh đổi giữa consistency và performance khi thiết kế",
            "Trading off consistency against performance in a design",
            "Giải quyết vấn đề & thuật toán"),
        new("debug-production-system",
            "Gỡ lỗi một hệ thống đang chạy thật trong production",
            "Debugging a live production system",
            "Giải quyết vấn đề & thuật toán"),
    ];

    private static readonly TopicRow[] BeSenior =
    [
        new("multi-service-architecture",
            "Thiết kế kiến trúc hệ thống nhiều service",
            "Designing the architecture for a multi-service system",
            "Thiết kế hệ thống & CSDL"),
        new("data-sync-across-services",
            "Cơ chế đồng bộ dữ liệu giữa các service",
            "Mechanisms for keeping data in sync across services",
            "Chiều sâu kỹ thuật"),
        new("idempotency",
            "Idempotency khi thiết kế API/service",
            "Idempotency when designing an API or service",
            "Chiều sâu kỹ thuật"),
        new("retry-backoff",
            "Chiến lược retry và backoff khi gọi service khác",
            "Retry and backoff strategy when calling another service",
            "Chiều sâu kỹ thuật"),
        new("storage-tradeoff-at-scale",
            "Đánh giá đánh đổi giữa các mô hình lưu trữ ở quy mô lớn",
            "Weighing storage-model trade-offs at large scale",
            "Thiết kế hệ thống & CSDL"),
        new("production-incident",
            "Xử lý sự cố sản xuất (production incident)",
            "Handling a production incident",
            "Giải quyết vấn đề & thuật toán"),
        new("fault-tolerant-design",
            "Thiết kế hệ thống chịu lỗi (fault-tolerant)",
            "Designing a fault-tolerant system",
            "Thiết kế hệ thống & CSDL"),
        new("ops-cost-vs-complexity",
            "Đánh đổi giữa chi phí vận hành và độ phức tạp kỹ thuật",
            "Trading off operating cost against technical complexity",
            "Giải quyết vấn đề & thuật toán"),
    ];

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // FE — Frontend. 3 tiêu chí WhenTargeted: "Chiều sâu kỹ thuật" · "Giải quyết vấn đề" ·
    // "Ý thức UI/UX & accessibility".
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private static readonly TopicRow[] FeFresher =
    [
        new("semantic-html",
            "HTML semantic: chọn đúng thẻ ngữ nghĩa cho một đoạn nội dung",
            "Semantic HTML: picking the right tag for a piece of content",
            "Ý thức UI/UX & accessibility"),
        new("box-model",
            "CSS box model: margin, border, padding hoạt động thế nào",
            "The CSS box model: how margin, border, and padding work",
            "Chiều sâu kỹ thuật"),
        new("flexbox-center",
            "Căn giữa một phần tử bằng flexbox",
            "Centering an element with flexbox",
            "Ý thức UI/UX & accessibility"),
        new("let-vs-const",
            "Khác nhau giữa let và const trong JavaScript",
            "The difference between let and const in JavaScript",
            "Chiều sâu kỹ thuật"),
        new("simple-function",
            "Viết một hàm JavaScript xử lý dữ liệu đơn giản",
            "Writing a simple JavaScript function to process data",
            "Chiều sâu kỹ thuật"),
        new("dom-manipulation",
            "Thao tác DOM: chọn và đổi nội dung một phần tử",
            "DOM manipulation: selecting and changing an element's content",
            "Chiều sâu kỹ thuật"),
        new("fetch-api",
            "Gọi một API bằng fetch và đọc kết quả trả về",
            "Calling an API with fetch and reading the response",
            "Giải quyết vấn đề"),
        new("element-not-showing",
            "Xử lý khi một phần tử không hiển thị đúng như mong đợi",
            "Handling a case where an element does not display as expected",
            "Giải quyết vấn đề"),
    ];

    private static readonly TopicRow[] FeJunior =
    [
        new("full-feature-ui",
            "Dựng UI hoàn chỉnh cho một tính năng bằng framework",
            "Building a complete UI for one feature with a framework",
            "Chiều sâu kỹ thuật"),
        new("local-state",
            "Quản lý state cục bộ trong component",
            "Managing local state inside a component",
            "Chiều sâu kỹ thuật"),
        new("form-validate",
            "Xử lý form và validate dữ liệu nhập",
            "Handling forms and validating input",
            "Chiều sâu kỹ thuật"),
        new("async-loading-error",
            "Gọi API bất đồng bộ và xử lý trạng thái loading/error",
            "Calling an API asynchronously and handling loading/error states",
            "Chiều sâu kỹ thuật"),
        new("responsive-broken-layout",
            "Xử lý layout vỡ hoặc chưa responsive",
            "Fixing a broken or non-responsive layout",
            "Ý thức UI/UX & accessibility"),
        new("unnecessary-rerender",
            "Chẩn đoán vì sao một component re-render không cần thiết",
            "Diagnosing why a component re-renders unnecessarily",
            "Giải quyết vấn đề"),
        new("race-condition-display",
            "Chẩn đoán dữ liệu hiển thị sai do race condition khi gọi API",
            "Diagnosing data shown incorrectly because of a race condition when calling an API",
            "Giải quyết vấn đề"),
        new("cross-device-ui-fix",
            "Sửa một lỗi UI thường gặp trên nhiều kích thước màn hình",
            "Fixing a common UI bug across different screen sizes",
            "Ý thức UI/UX & accessibility"),
    ];

    private static readonly TopicRow[] FeMiddle =
    [
        new("reusable-component-structure",
            "Thiết kế cấu trúc component tái sử dụng",
            "Designing a reusable component structure",
            "Chiều sâu kỹ thuật"),
        new("global-state-choice",
            "Chọn giải pháp quản lý state toàn cục phù hợp",
            "Choosing a suitable global state-management solution",
            "Giải quyết vấn đề"),
        new("memoization",
            "Tối ưu hiệu năng render bằng memoization",
            "Optimizing render performance with memoization",
            "Chiều sâu kỹ thuật"),
        new("lazy-load",
            "Tối ưu hiệu năng render bằng lazy-load",
            "Optimizing render performance with lazy-loading",
            "Chiều sâu kỹ thuật"),
        new("accessibility",
            "Đảm bảo accessibility cho một giao diện",
            "Making an interface accessible",
            "Ý thức UI/UX & accessibility"),
        new("component-testing",
            "Viết test cho một component",
            "Writing tests for a component",
            "Chiều sâu kỹ thuật"),
        new("split-component-decision",
            "Quyết định khi nào nên tách nhỏ một component",
            "Deciding when to split a component into smaller pieces",
            "Giải quyết vấn đề"),
        new("real-performance-issue",
            "Gỡ một vấn đề hiệu năng thực tế trên giao diện",
            "Fixing a real-world performance issue in the UI",
            "Giải quyết vấn đề"),
    ];

    private static readonly TopicRow[] FeSenior =
    [
        new("micro-frontend-architecture",
            "Thiết kế kiến trúc micro-frontend cho hệ thống lớn",
            "Designing a micro-frontend architecture for a large system",
            "Chiều sâu kỹ thuật"),
        new("module-federation",
            "Module federation: khái niệm và tình huống áp dụng",
            "Module federation: the concept and when to use it",
            "Chiều sâu kỹ thuật"),
        new("caching-cdn-strategy",
            "Chiến lược caching và CDN cho frontend",
            "Caching and CDN strategy for a frontend",
            "Chiều sâu kỹ thuật"),
        new("build-deploy-standard",
            "Chuẩn hoá quy trình build và deploy cho nhiều team",
            "Standardizing the build and deploy process across teams",
            "Giải quyết vấn đề"),
        new("production-performance-incident",
            "Xử lý sự cố hiệu năng ở production",
            "Handling a production performance incident",
            "Giải quyết vấn đề"),
        new("ux-vs-technical-cost",
            "Đánh đổi giữa trải nghiệm người dùng và chi phí kỹ thuật",
            "Trading off user experience against technical cost",
            "Ý thức UI/UX & accessibility"),
        new("maintainability-multi-team",
            "Đảm bảo khả năng bảo trì frontend ở quy mô nhiều team",
            "Ensuring frontend maintainability at multi-team scale",
            "Giải quyết vấn đề"),
        new("tech-leadership-standardization",
            "Dẫn dắt kỹ thuật và chuẩn hoá cách làm cho nhiều team",
            "Providing technical leadership and standardizing practices across teams",
            "Giải quyết vấn đề"),
    ];
}
